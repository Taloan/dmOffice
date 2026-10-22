// Office-Dokumente im Browser bearbeiten: Collabora Online/CODE (oder ONLYOFFICE ueber WOPI) im Vollbild innerhalb von
// dmOffice. Der Server gibt Adresse und ein kurzlebiges Token; das Token geht per POST in das iframe, nie in die Adresse.
import { Button, MessageBar, MessageBarBody, Spinner, Text, makeStyles, tokens } from '@fluentui/react-components'
import { ArrowLeft24Regular } from '@fluentui/react-icons'
import { useEffect, useRef, useState } from 'react'
import { api } from '../api'
import { t, tError } from '../i18n'
import { extOf } from './common'

export interface OfficeConfig { enabled: boolean; product?: string; edit?: string[]; view?: string[]; create?: string[]; error?: string }
/** WOPI (Collabora): url + token per POST ins iframe. ONLYOFFICE Docs-API: api + (signierte) config fuer DocsAPI.DocEditor. */
export interface OfficeSession {
  url?: string; token?: string; ttl?: number; api?: string; config?: Record<string, any>
  name: string; path?: string; write: boolean; product?: string
}

/** api.js des Document Servers einmal laden. */
const scripts: Record<string, Promise<void>> = {}
function loadScript(src: string): Promise<void> {
  scripts[src] ??= new Promise((resolve, reject) => {
    const el = document.createElement('script')
    el.src = src
    el.onload = () => resolve()
    el.onerror = () => { delete scripts[src]; reject(new Error(t('Der Office-Server ist gerade nicht erreichbar.'))) }
    document.head.appendChild(el)
  })
  return scripts[src]
}

let cached: Promise<OfficeConfig> | null = null

/** Ist ein Office-Server eingestellt, und welche Endungen kann er? Einmal je Seitenaufruf gefragt. */
export function useOffice(): OfficeConfig | null {
  const [cfg, setCfg] = useState<OfficeConfig | null>(null)
  useEffect(() => {
    cached ??= api.get<OfficeConfig>('/api/office/config').catch(() => ({ enabled: false }))
    let live = true
    cached.then(c => { if (live) setCfg(c) })
    return () => { live = false }
  }, [])
  return cfg
}

/** Nach einer Aenderung in der Verwaltung neu fragen. */
export function forgetOffice() { cached = null }

/** 'edit', 'view' oder null - je nach Endung und was der Office-Server kann. */
export function officeMode(i: { name: string; isDir: boolean }, cfg: OfficeConfig | null): 'edit' | 'view' | null {
  if (!cfg?.enabled || i.isDir) return null
  const ext = extOf(i.name).slice(1)
  if (cfg.edit?.includes(ext)) return 'edit'
  if (ext !== 'pdf' && cfg.view?.includes(ext)) return 'view'
  return null
}

const useStyles = makeStyles({
  root: { position: 'fixed', inset: 0, zIndex: 1000, display: 'flex', flexDirection: 'column', backgroundColor: tokens.colorNeutralBackground1 },
  bar: {
    display: 'flex', alignItems: 'center', gap: '8px', padding: '2px 8px', minHeight: '40px', flexShrink: 0,
    paddingTop: 'calc(2px + env(safe-area-inset-top, 0px))', borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  name: { flex: 1, minWidth: 0, fontWeight: 600, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  stage: { flex: 1, minHeight: 0, position: 'relative' },
  frame: { position: 'absolute', inset: 0, width: '100%', height: '100%', border: 'none' },
  center: { position: 'absolute', inset: 0, display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', gap: '12px', padding: '24px' },
})

/** Editor im Vollbild. open() holt die Sitzung (angemeldet /api/office/open, oeffentlich /api/public/.../office). */
export function OfficeEditor({ name, open, onClose }: { name: string; open: () => Promise<OfficeSession>; onClose: () => void }) {
  const s = useStyles()
  const [session, setSession] = useState<OfficeSession | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loaded, setLoaded] = useState(false)
  const form = useRef<HTMLFormElement>(null)
  const frame = useRef<HTMLIFrameElement>(null)
  const frameName = useRef('office-' + Math.random().toString(36).slice(2))
  const closeRef = useRef(onClose)
  closeRef.current = onClose

  useEffect(() => {
    let live = true
    open().then(x => { if (live) setSession(x) }).catch(e => { if (live) setError(tError(e?.message || String(e))) })
    return () => { live = false }
  }, []) // eslint-disable-line react-hooks/exhaustive-deps

  // Sobald das iframe da ist: Token per POST hinein (so steht es in keiner Adresse und keinem Verlauf)
  useEffect(() => { if (session?.url) form.current?.submit() }, [session])

  // ONLYOFFICE Docs-API: api.js laden, Editor in den Platzhalter setzen
  const holder = useRef<HTMLDivElement>(null)
  useEffect(() => {
    if (!session?.api || !session.config) return
    let editor: any = null
    let live = true
    loadScript(session.api).then(() => {
      if (!live || !holder.current) return
      const DocsAPI = (window as any).DocsAPI
      if (!DocsAPI?.DocEditor) throw new Error(t('Der Office-Server ist gerade nicht erreichbar.'))
      // Der Editor ersetzt seinen Platzhalter durch ein iframe - darum ein eigenes Element, das React nicht kennt
      const place = document.createElement('div')
      place.id = frameName.current
      holder.current.replaceChildren(place)
      editor = new DocsAPI.DocEditor(place.id, {
        ...session.config, width: '100%', height: '100%',
        events: {
          onAppReady: () => setLoaded(true),
          onDocumentReady: () => setLoaded(true),
          onRequestClose: () => closeRef.current(),
          onError: (e: any) => setError(e?.data?.errorDescription || t('Fehler im Office-Server')),
        },
      })
    }).catch(e => { if (live) setError(e?.message || String(e)) })
    return () => { live = false; try { editor?.destroyEditor() } catch { /* schon weg */ } }
  }, [session])

  // Nachrichten des Editors (WOPI PostMessage): geladen, Schliessen-Knopf in Collabora
  useEffect(() => {
    if (!session?.url) return
    const origin = new URL(session.url).origin
    const on = (e: MessageEvent) => {
      if (e.origin !== origin || e.source !== frame.current?.contentWindow) return
      let msg: any = e.data
      if (typeof msg === 'string') { try { msg = JSON.parse(msg) } catch { return } }
      const id = msg?.MessageId
      if (id === 'App_LoadingStatus') {
        setLoaded(true)
        if (msg.Values?.Status === 'Frame_Ready' || msg.Values?.Status === 'Document_Loaded')
          frame.current?.contentWindow?.postMessage(JSON.stringify({ MessageId: 'Host_PostmessageReady', SendTime: Date.now(), Values: {} }), origin)
      } else if (id === 'UI_Close' || id === 'close') closeRef.current()
    }
    window.addEventListener('message', on)
    // Kein Signal vom Editor (anderer Office-Server): die Ladeanzeige nach einer Weile trotzdem weg
    const timer = window.setTimeout(() => setLoaded(true), 15000)
    return () => { window.removeEventListener('message', on); window.clearTimeout(timer) }
  }, [session])

  return (
    <div className={s.root} role="dialog" aria-label={name}>
      <div className={s.bar}>
        <Button appearance="subtle" icon={<ArrowLeft24Regular />} onClick={onClose} aria-label={t('Zurück')}>{t('Zurück')}</Button>
        <Text className={s.name}>{name}</Text>
        {session && !session.write && <Text size={200}>{t('Nur ansehen')}</Text>}
      </div>
      <div className={s.stage}>
        {session?.api && <div ref={holder} className={s.frame} />}
        {session?.url && <>
          <form ref={form} action={session.url} method="post" target={frameName.current} style={{ display: 'none' }}>
            <input type="hidden" name="access_token" value={session.token} />
            <input type="hidden" name="access_token_ttl" value={String(session.ttl)} />
          </form>
          <iframe ref={frame} name={frameName.current} className={s.frame} title={name}
            allow="clipboard-read; clipboard-write; fullscreen" allowFullScreen />
        </>}
        {error ? (
          <div className={s.center}>
            <MessageBar intent="error"><MessageBarBody>{error}</MessageBarBody></MessageBar>
            <Button onClick={onClose}>{t('Zurück')}</Button>
          </div>
        ) : !loaded && (
          <div className={s.center} style={{ pointerEvents: 'none' }}>
            <Spinner label={t('Office wird geladen …')} />
          </div>
        )}
      </div>
    </div>
  )
}
