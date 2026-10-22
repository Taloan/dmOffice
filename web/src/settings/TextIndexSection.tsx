import { Button, MessageBar, MessageBarBody, Spinner, Switch, Text, makeStyles, tokens } from '@fluentui/react-components'
import { ArrowClockwise20Regular } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { fmtDay, fmtTime } from '../shell/hooks'
import { Hint, Section } from './parts'

interface Status {
  enabled: boolean; ocr: boolean; ocrAvailable: boolean; ocrReason: string | null; ocrLanguages: string
  done: number; withText: number; byOcr: number; pending: number; errors: number; skipped: number; lastRun: string | null
  recentErrors: { kind: string; name: string; path: string | null; note: string | null }[]
}

const useStyles = makeStyles({
  stats: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(120px, 1fr))', gap: '8px', maxWidth: '640px' },
  stat: { display: 'flex', flexDirection: 'column', padding: '8px 10px', borderRadius: tokens.borderRadiusMedium, backgroundColor: tokens.colorNeutralBackground2 },
  num: { fontSize: '20px', fontWeight: 600, lineHeight: '26px' },
  errors: { margin: 0, paddingLeft: '18px', color: tokens.colorNeutralForeground3, fontSize: '12px' },
  sub: { paddingLeft: '44px', display: 'flex', flexDirection: 'column', gap: '4px' },
})

/** Einstellungen -> Suchen: Inhalte von Anhaengen und Dateien durchsuchbar machen, auf Wunsch mit OCR. */
export function TextIndexSection() {
  const s = useStyles()
  const { fail } = useApp()
  const [st, setSt] = useState<Status | null>(null)
  const [busy, setBusy] = useState(false)

  const load = () => api.get<Status>('/api/settings/textindex').then(setSt).catch(fail)
  useEffect(() => { load() }, []) // eslint-disable-line react-hooks/exhaustive-deps
  // Waehrend die Warteschlange laeuft, den Stand alle paar Sekunden nachladen
  useEffect(() => {
    if (!st?.enabled || st.pending === 0) return
    const id = setInterval(() => { api.get<Status>('/api/settings/textindex').then(setSt).catch(() => {}) }, 4000)
    return () => clearInterval(id)
  }, [st?.enabled, st?.pending === 0]) // eslint-disable-line react-hooks/exhaustive-deps

  const save = async (enabled: boolean, ocr: boolean) => {
    setBusy(true)
    try { setSt(await api.put<Status>('/api/settings/textindex', { enabled, ocr })) } catch (e) { fail(e) } finally { setBusy(false) }
  }
  const rebuild = async () => {
    setBusy(true)
    try { setSt(await api.post<Status>('/api/settings/textindex/rebuild', {})) } catch (e) { fail(e) } finally { setBusy(false) }
  }

  if (!st) return <Section title={t('Inhalte von Anhängen und Dateien')}><Spinner size="tiny" /></Section>
  const last = st.lastRun ? new Date(st.lastRun) : null
  return (
    <Section title={t('Inhalte von Anhängen und Dateien')} last
      hint={t('Dann findet die Suche Rechnungen, Verträge und Scans auch über ihren Inhalt – in den Anhängen deiner Mails, in Bildern und Anhängen deiner Notizen und in der Dateiablage. Gelesen wird nur auf diesem Server, nichts geht an fremde Dienste.')}>
      <Switch label={t('Inhalte von Anhängen und Dateien durchsuchbar machen')} checked={st.enabled} disabled={busy}
        onChange={(_, d) => save(d.checked, d.checked && st.ocr)} />
      <div className={s.sub}>
        <Switch label={t('Auch Texterkennung (OCR) für Bilder und gescannte PDFs')} checked={st.ocr && st.ocrAvailable}
          disabled={busy || !st.enabled || !st.ocrAvailable} onChange={(_, d) => save(true, d.checked)} />
        {!st.ocrAvailable
          ? <Hint>{t('Auf diesem Server nicht verfügbar: {reason} Im Docker-Image von dmOffice ist Tesseract enthalten.', { reason: st.ocrReason ?? '' })}</Hint>
          : <Hint>{t('Erkennt Text in Fotos und Scans (Deutsch und Englisch). Braucht deutlich mehr Rechenzeit als das Lesen von Textdateien – läuft im Hintergrund mit niedriger Priorität.')}</Hint>}
      </div>
      {st.enabled && <>
        <div className={s.stats}>
          <div className={s.stat}><span className={s.num}>{st.done}</span><Text size={200}>{t('erledigt')}</Text></div>
          <div className={s.stat}><span className={s.num}>{st.withText}</span><Text size={200}>{t('mit Text')}{st.byOcr > 0 ? ` (${t('{n} per OCR', { n: st.byOcr })})` : ''}</Text></div>
          <div className={s.stat}><span className={s.num}>{st.pending}</span><Text size={200}>{t('offen')}</Text></div>
          <div className={s.stat}><span className={s.num}>{st.errors}</span><Text size={200}>{t('Fehler')}</Text></div>
        </div>
        <Hint>
          {last ? t('Letzter Lauf: {day}, {time}', { day: fmtDay(last), time: fmtTime(last) }) : t('Noch kein Lauf.')}
          {st.pending > 0 && ' ' + t('Der Bestand wird nach und nach gelesen – Neues kommt zuerst dran.')}
        </Hint>
        {st.recentErrors.length > 0 && (
          <MessageBar intent="warning"><MessageBarBody>
            {t('Diese Inhalte ließen sich nicht lesen:')}
            <ul className={s.errors}>
              {st.recentErrors.map((e, i) => <li key={i}>{e.path ?? e.name}{e.note ? ` – ${e.note}` : ''}</li>)}
            </ul>
          </MessageBarBody></MessageBar>
        )}
        <div>
          <Button icon={busy ? <Spinner size="tiny" /> : <ArrowClockwise20Regular />} disabled={busy} onClick={rebuild}>{t('Index neu aufbauen')}</Button>
        </div>
        <Hint>{t('Ausschalten löscht den Index; die Mails und Dateien selbst bleiben unverändert.')}</Hint>
      </>}
    </Section>
  )
}
