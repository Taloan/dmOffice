// Einstellungen → Tresor (docs/tresor-konzept.md, „Einstellungen“): einrichten, Tresor-Passwort ändern, Wiederherstellung,
// automatisch schließen, Schließen bei Zurücksetzen durch die Verwaltung, Geräte mit offenem Tresor, Belegung, Tresor löschen.
import {
  Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Dropdown, Field, Input, MessageBar, MessageBarBody,
  Option, Text, makeStyles, tokens,
} from '@fluentui/react-components'
import { LockClosed20Regular, LockOpen20Regular } from '@fluentui/react-icons'
import { useCallback, useEffect, useState } from 'react'
import { api, fmtSize } from '../api'
import { useApp } from '../App'
import { locale, t } from '../i18n'
import { askConfirm } from '../shell/context'
import { copyText, saveText } from '../shell/browser'
import { OPEN_COLOR, TRESOR_EVENT, TresorState, addTresorPasskey, closeTresor, deviceName, forgetThisDevice, passkeysPossible, requestTresorOpen, tresorChanged, tresorState } from '../tresor/tresor'
import { Section } from './parts'

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '12px', maxWidth: '440px' },
  row: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
  codes: { display: 'grid', gridTemplateColumns: 'repeat(2, 1fr)', gap: '4px 16px', fontFamily: 'Consolas, monospace', fontSize: '15px', padding: '12px', backgroundColor: tokens.colorNeutralBackground3, borderRadius: tokens.borderRadiusMedium },
  key: { fontFamily: 'Consolas, monospace', fontSize: '15px', padding: '12px', backgroundColor: tokens.colorNeutralBackground3, borderRadius: tokens.borderRadiusMedium, wordBreak: 'break-all' },
  device: { display: 'flex', gap: '8px', alignItems: 'center', padding: '6px 10px', borderRadius: tokens.borderRadiusMedium, backgroundColor: tokens.colorNeutralBackground2, maxWidth: '560px' },
})

const AUTO: { value: NonNullable<TresorState['settings']>['autoClose']; label: string }[] = [
  { value: '1h', label: t('nach 1 Stunde ohne Nutzung') }, { value: '8h', label: t('nach 8 Stunden ohne Nutzung') },
  { value: '1d', label: t('nach 1 Tag ohne Nutzung') }, { value: 'never', label: t('nie') },
]
const when = (iso: string) => new Date(iso).toLocaleString(locale, { dateStyle: 'short', timeStyle: 'short' })

/** Text zum Ausdrucken bzw. Speichern: Codes und Wiederherstellungsschlüssel. */
function sheet(codes: string[] | null, key: string | null) {
  const lines = [t('dmOffice – Wiederherstellung des Tresors'), new Date().toLocaleDateString(locale), '']
  if (key) lines.push(t('Wiederherstellungsschlüssel (beliebig oft verwendbar):'), key, '')
  if (codes) lines.push(t('Wiederherstellungscodes (je einmal verwendbar):'), ...codes, '')
  return lines.join('\n')
}

function printSheet(text: string) {
  const w = window.open('', '_blank', 'width=600,height=700')
  if (!w) return
  const pre = w.document.createElement('pre')
  pre.style.cssText = 'font: 15px Consolas, monospace; white-space: pre-wrap; padding: 24px'
  pre.textContent = text
  w.document.title = t('Tresor – Wiederherstellung')
  w.document.body.appendChild(pre)
  w.focus()
  w.print()
}

/** Codes und Schlüssel anzeigen: kopieren, speichern, drucken. */
function RecoverySheet({ codes, recoveryKey }: { codes: string[] | null; recoveryKey: string | null }) {
  const s = useStyles()
  const { notify } = useApp()
  const text = sheet(codes, recoveryKey)
  const copy = async (value: string) => (await copyText(value))
    ? notify(t('Kopiert'))
    : notify(t('Kopieren ging nicht'), t('Bitte markieren und mit Strg+C kopieren.'), 'warning')
  return (
    <div className={s.form}>
      {recoveryKey && <><Text weight="semibold">{t('Wiederherstellungsschlüssel')}</Text><div className={s.key}>{recoveryKey}</div></>}
      {codes && <><Text weight="semibold">{t('Wiederherstellungscodes – jeder gilt einmal')}</Text><div className={s.codes}>{codes.map(c => <span key={c}>{c}</span>)}</div></>}
      <div className={s.row}>
        <Button onClick={() => printSheet(text)}>{t('Drucken')}</Button>
        <Button onClick={() => saveText('tresor-wiederherstellung.txt', text)}>{t('Als Datei speichern')}</Button>
        {/* Kopieren: nur der Schlüssel bzw. nur die Codes - so passen sie in ein Feld des Passwortmanagers (Ausdruck und Datei: alles) */}
        {recoveryKey && <Button onClick={() => copy(recoveryKey)}>{t('Schlüssel kopieren')}</Button>}
        {codes && <Button onClick={() => copy(codes.join('\n'))}>{t('Codes kopieren')}</Button>}
      </div>
    </div>
  )
}

export function TresorSettings() {
  const s = useStyles()
  const { fail, notify, me } = useApp()
  const [st, setSt] = useState<TresorState | null>(null)
  const [shown, setShown] = useState<{ codes: string[] | null; key: string | null } | null>(null)
  const load = useCallback(() => tresorState().then(setSt).catch(fail), []) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    load()
    window.addEventListener(TRESOR_EVENT, load)
    return () => window.removeEventListener(TRESOR_EVENT, load)
  }, [load])
  if (!st) return null

  const title = <Text size={600} weight="semibold">{t('Tresor')}</Text>
  const intro = <Text>{t('Dateien und notizi-Datenbanken im Tresor werden mit einem eigenen Tresor-Passwort verschlüsselt. Der Schlüssel wird nicht auf dem Server gespeichert. Zugang zum Inhalt ermöglichen ausschließlich das Tresor-Passwort, ein Wiederherstellungscode oder der Wiederherstellungsschlüssel; das gilt auch für Administratoren des Servers.')}</Text>

  if (!st.exists) return <>{title}{intro}<Setup onDone={(codes, key) => { setShown({ codes, key }); load(); tresorChanged() }} /></>

  // Gerade eingerichtet oder neue Codes: einmal zeigen
  if (shown) return (
    <>
      {title}
      <MessageBar intent="warning"><MessageBarBody>{t('Bitte jetzt ausdrucken oder sicher ablegen. Ohne Tresor-Passwort, Codes und Wiederherstellungsschlüssel ist der Inhalt verloren.')}</MessageBarBody></MessageBar>
      <RecoverySheet codes={shown.codes} recoveryKey={shown.key} />
      <div><Button appearance="primary" onClick={() => setShown(null)}>{t('Erledigt')}</Button></div>
    </>
  )

  const open = !!st.open
  const settings = st.settings!
  const put = async (body: object) => {
    try { setSt(await api.put<TresorState>('/api/tresor/settings', body)); tresorChanged() } catch (e) { fail(e) }
  }
  const needOpen = (f: () => void) => async () => { if (open || await requestTresorOpen()) f() }

  return (
    <>
      {title}
      {intro}
      <div className={s.row}>
        {open
          ? <><LockOpen20Regular style={{ color: OPEN_COLOR }} /><Text>{t('Der Tresor ist auf diesem Gerät offen.')}</Text>
            <Button onClick={() => closeTresor().catch(fail)}>{t('Tresor schließen')}</Button></>
          : <><LockClosed20Regular /><Text>{t('Der Tresor ist auf diesem Gerät geschlossen.')}</Text>
            <Button appearance="primary" onClick={() => requestTresorOpen()}>{t('Tresor öffnen')}</Button></>}
      </div>

      <Section title={t('Öffnen')} hint={t('Mit einem Passkey öffnest du den Tresor über Windows Hello, Fingerabdruck oder die PIN des Geräts statt mit dem Tresor-Passwort.')}>
        <div className={s.form}>
          {(st.passkeys ?? []).map(p => (
            <div key={p.id} className={s.device}>
              <Text style={{ flex: 1 }}>{p.name ?? t('Passkey')}<br />
                <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('seit {date}', { date: when(p.created) })}</Text></Text>
              <Button size="small" onClick={async () => {
                if (!await askConfirm('', t('Diesen Passkey entfernen? Den Tresor öffnet er danach nicht mehr.'), t('Entfernen'))) return
                try { await api.del(`/api/tresor/remembered/${p.id}`); load() } catch (e) { fail(e) }
              }}>{t('Entfernen')}</Button>
            </div>
          ))}
          {passkeysPossible()
            ? <div><Button onClick={needOpen(async () => {
              try { if (await addTresorPasskey(st.user, me?.login ?? '')) notify(t('Passkey hinterlegt')) }
              catch (e) { if (!(e instanceof Error && e.name === 'NotAllowedError')) fail(e) }
            })}>{t('Passkey hinzufügen')}</Button></div>
            : <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('Passkeys gehen nur über eine sichere Verbindung (https).')}</Text>}
        </div>
      </Section>

      <Section title={t('Tresor-Passwort ändern')}>
        <ChangePassword />
      </Section>

      <Section title={t('Wiederherstellung')} hint={t('Mit einem Wiederherstellungscode oder dem Wiederherstellungsschlüssel öffnest du den Tresor, wenn du das Tresor-Passwort vergessen hast.')}>
        <div className={s.form}>
          <Text>{t('Noch gültige Wiederherstellungscodes: {n} von 10', { n: st.codesLeft ?? 0 })}</Text>
          <div className={s.row}>
            <Button onClick={needOpen(async () => {
              if (!await askConfirm('', t('Zehn neue Codes erzeugen? Die bisherigen gelten danach nicht mehr.'), t('Neu erzeugen'))) return
              try { const r = await api.post<{ recoveryCodes: string[] }>('/api/tresor/codes'); setShown({ codes: r.recoveryCodes, key: null }); load() } catch (e) { fail(e) }
            })}>{t('Codes neu erzeugen')}</Button>
            <Button onClick={needOpen(async () => {
              try { const r = await api.get<{ recoveryKey: string }>('/api/tresor/recovery-key'); setShown({ codes: null, key: r.recoveryKey }) } catch (e) { fail(e) }
            })}>{t('Wiederherstellungsschlüssel anzeigen und drucken')}</Button>
            <Button onClick={needOpen(async () => {
              if (!await askConfirm('', t('Einen neuen Wiederherstellungsschlüssel erzeugen? Der bisherige gilt danach nicht mehr.'), t('Neu erzeugen'))) return
              try { const r = await api.post<{ recoveryKey: string }>('/api/tresor/recovery-key'); setShown({ codes: null, key: r.recoveryKey }) } catch (e) { fail(e) }
            })}>{t('Wiederherstellungsschlüssel neu erzeugen')}</Button>
          </div>
        </div>
      </Section>

      <Section title={t('Automatisch schließen')} hint={t('Gilt je Gerät. Bei „nie“ kann sich ein Gerät den Tresor merken und öffnet ihn nach einem Neustart des Servers von selbst wieder.')}>
        <div className={s.form}>
          <Dropdown value={AUTO.find(a => a.value === settings.autoClose)?.label} selectedOptions={[settings.autoClose]} disabled={!open}
            onOptionSelect={async (_, d) => {
              const v = d.optionValue as typeof settings.autoClose
              if (v !== 'never') forgetThisDevice(st.user)
              await put({ autoClose: v })
            }}>
            {AUTO.map(a => <Option key={a.value} value={a.value}>{a.label}</Option>)}
          </Dropdown>
          <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('Setzt ein Administrator dein Passwort oder die Zwei-Faktor-Anmeldung zurück, schließt sich der Tresor auf allen Geräten.')}</Text>
          {!open && <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('Ändern geht nur bei offenem Tresor.')}</Text>}
        </div>
      </Section>

      <Section title={t('Nextcloud und WebDAV')} hint={t('Der Tresor liegt als Cryptomator-Tresor im Ordner „dmVAULT“ deiner Dateien.')}>
        <div className={s.form}>
          {st.oldFormat && <MessageBar intent="warning"><MessageBarBody>{t('Der Tresor ist noch nicht als Cryptomator-Tresor angelegt, der Ordner „dmVAULT“ fehlt deshalb. Umgestellt wird er beim nächsten Öffnen; vorhandene Dateien werden dabei übernommen.')}</MessageBarBody></MessageBar>}
          {st.needsPassword && <MessageBar intent="info"><MessageBarBody>{t('Für Cryptomator am Rechner fehlt noch die Datei masterkey.cryptomator. Sie entsteht beim nächsten Öffnen mit dem Tresor-Passwort.')}</MessageBarBody></MessageBar>}
          <Text>{settings.dav ? t('Der Ordner „dmVAULT“ ist für Nextcloud und WebDAV sichtbar.') : t('Der Ordner „dmVAULT“ ist für Nextcloud und WebDAV ausgeblendet.')}</Text>
          <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('Ein- und ausblenden lässt er sich in der Dateiablage über das Kontextmenü (Rechtsklick) auf „Tresor“.')}</Text>
          {settings.dav && <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('Für Zugriff ohne Server im Nextcloud-Client den Ordner dmVAULT immer lokal verfügbar halten (nicht als virtuelle Dateien). Geöffnet wird er mit Cryptomator und dem Tresor-Passwort.')}</Text>}
        </div>
      </Section>

      <Section title={t('Geräte mit offenem Tresor')}>
        <div className={s.form}>
          {(st.devices ?? []).length === 0 && <Text>{t('Auf keinem Gerät offen.')}</Text>}
          {(st.devices ?? []).map(d => (
            <div key={d.id} className={s.device}>
              <Text style={{ flex: 1 }}>{d.name}{d.mine ? ' · ' + t('dieses Gerät') : ''}<br />
                <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('geöffnet {at}, zuletzt genutzt {used}', { at: when(d.at), used: when(d.used) })}</Text></Text>
              <Button size="small" onClick={async () => {
                try { if (d.mine) await closeTresor(); else { await api.post(`/api/tresor/close/${d.id}`); tresorChanged() } } catch (e) { fail(e) }
              }}>{t('Schließen')}</Button>
            </div>
          ))}
          {(st.devices ?? []).length > 1 && <div><Button onClick={async () => { try { await api.post('/api/tresor/close-all'); tresorChanged() } catch (e) { fail(e) } }}>{t('Auf allen Geräten schließen')}</Button></div>}
          {(st.remembered ?? []).length > 0 && <>
            <Text weight="semibold">{t('Geräte, die sich den Tresor merken')}</Text>
            {st.remembered!.map(r => (
              <div key={r.id} className={s.device}>
                <Text style={{ flex: 1 }}>{r.name ?? t('Gerät')} · {r.kind === 'biometric' ? t('Fingerabdruck oder Gesicht') : t('öffnet von selbst')}<br />
                  <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('seit {date}', { date: when(r.created) })}</Text></Text>
                <Button size="small" onClick={async () => { try { await api.del(`/api/tresor/remembered/${r.id}`); load() } catch (e) { fail(e) } }}>{t('Entfernen')}</Button>
              </div>
            ))}
          </>}
        </div>
      </Section>

      <Section title={t('Belegung')}>
        <Text>{t('Der Tresor belegt {size}.', { size: fmtSize(st.size ?? 0) })}</Text>
      </Section>

      <Section title={t('Tresor löschen')} hint={t('Löscht den gesamten Inhalt des Tresors endgültig.')} last>
        <DeleteTresor onDone={() => { notify(t('Tresor gelöscht')); load(); tresorChanged() }} />
      </Section>
    </>
  )
}

function Setup({ onDone }: { onDone: (codes: string[], key: string) => void }) {
  const s = useStyles()
  const [pw, setPw] = useState('')
  const [pw2, setPw2] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const go = async () => {
    if (pw !== pw2) { setError(t('Die beiden Passwörter stimmen nicht überein.')); return }
    setBusy(true); setError(null)
    try {
      // Direkt, nicht im Umschlag: das Cookie des Tresors muss ankommen
      const r = await fetch('/api/tresor/setup', { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ password: pw, name: deviceName() }) })
      const data = await r.json().catch(() => ({}))
      if (!r.ok) throw new Error(data.error ?? t('Der Tresor ließ sich nicht einrichten.'))
      onDone(data.recoveryCodes, data.recoveryKey)
    } catch (e) { setError(e instanceof Error ? e.message : String(e)) }
    finally { setBusy(false) }
  }
  return (
    <Section title={t('Einrichten')} last>
      <form className={s.form} onSubmit={e => { e.preventDefault(); go() }}>
        <MessageBar intent="warning"><MessageBarBody>{t('Ohne Tresor-Passwort, Wiederherstellungscodes und Wiederherstellungsschlüssel kommt niemand mehr an den Inhalt – auch Administratoren nicht. Gleich nach dem Einrichten bekommst du Codes und Schlüssel zum Ausdrucken.')}</MessageBarBody></MessageBar>
        {error && <MessageBar intent="error"><MessageBarBody>{error}</MessageBarBody></MessageBar>}
        <Field label={t('Tresor-Passwort')} hint={t('Mindestens 10 Zeichen, nicht dasselbe wie zum Anmelden.')}>
          <Input type="password" value={pw} onChange={(_, d) => setPw(d.value)} autoComplete="new-password" />
        </Field>
        <Field label={t('Tresor-Passwort wiederholen')}>
          <Input type="password" value={pw2} onChange={(_, d) => setPw2(d.value)} autoComplete="new-password" />
        </Field>
        <div><Button appearance="primary" type="submit" disabled={!pw || busy}>{t('Tresor einrichten')}</Button></div>
      </form>
    </Section>
  )
}

function ChangePassword() {
  const s = useStyles()
  const { notify } = useApp()
  const [cur, setCur] = useState('')
  const [pw, setPw] = useState('')
  const [pw2, setPw2] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const go = async () => {
    if (pw !== pw2) { setError(t('Die beiden Passwörter stimmen nicht überein.')); return }
    setBusy(true); setError(null)
    try { await api.post('/api/tresor/password', { current: cur, password: pw }); setCur(''); setPw(''); setPw2(''); notify(t('Tresor-Passwort geändert')) }
    catch (e) { setError(e instanceof Error ? e.message : String(e)) }
    finally { setBusy(false) }
  }
  return (
    <form className={s.form} onSubmit={e => { e.preventDefault(); go() }}>
      {error && <MessageBar intent="error"><MessageBarBody>{error}</MessageBarBody></MessageBar>}
      <Field label={t('Bisheriges Tresor-Passwort')}><Input type="password" value={cur} onChange={(_, d) => setCur(d.value)} autoComplete="current-password" /></Field>
      <Field label={t('Neues Tresor-Passwort')} hint={t('Mindestens 10 Zeichen, nicht dasselbe wie zum Anmelden.')}><Input type="password" value={pw} onChange={(_, d) => setPw(d.value)} autoComplete="new-password" /></Field>
      <Field label={t('Neues Tresor-Passwort wiederholen')}><Input type="password" value={pw2} onChange={(_, d) => setPw2(d.value)} autoComplete="new-password" /></Field>
      <div><Button type="submit" disabled={!cur || !pw || busy}>{t('Tresor-Passwort ändern')}</Button></div>
    </form>
  )
}

/** Doppelte Rückfrage: erst bestätigen, dann das Tresor-Passwort eingeben. */
function DeleteTresor({ onDone }: { onDone: () => void }) {
  const [ask, setAsk] = useState(false)
  const [pw, setPw] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const go = async () => {
    setBusy(true); setError(null)
    try {
      const r = await fetch('/api/tresor', { method: 'DELETE', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ password: pw }) })
      if (!r.ok) throw new Error((await r.json().catch(() => ({}))).error ?? t('Der Tresor ließ sich nicht löschen.'))
      setAsk(false); onDone()
    } catch (e) { setError(e instanceof Error ? e.message : String(e)) }
    finally { setBusy(false) }
  }
  return <>
    <div><Button onClick={async () => {
      const text = t('Alles im Tresor wird endgültig gelöscht – Dateien und notizi-Datenbanken. Das lässt sich nicht rückgängig machen.')
      if (await askConfirm(t('Tresor löschen'), text, t('Weiter'))) { setPw(''); setError(null); setAsk(true) }
    }}>{t('Tresor löschen …')}</Button></div>
    {ask && <Dialog open onOpenChange={(_, d) => !d.open && setAsk(false)}>
      <DialogSurface style={{ maxWidth: 420 }}>
        <form onSubmit={e => { e.preventDefault(); if (pw) go() }}>
          <DialogBody>
            <DialogTitle>{t('Tresor endgültig löschen')}</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              {error && <MessageBar intent="error"><MessageBarBody>{error}</MessageBarBody></MessageBar>}
              <Field label={t('Zur Bestätigung das Tresor-Passwort')}><Input type="password" value={pw} onChange={(_, d) => setPw(d.value)} autoFocus autoComplete="off" /></Field>
            </DialogContent>
            <DialogActions>
              <Button onClick={() => setAsk(false)}>{t('Abbrechen')}</Button>
              <Button appearance="primary" type="submit" disabled={!pw || busy}>{t('Endgültig löschen')}</Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>}
  </>
}
