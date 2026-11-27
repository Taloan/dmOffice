// Plugins zur Laufzeit (S15/W1/W6): Liste vom Server, Sprachdatei und Web-Teil je eingeschaltetem Plugin. Nur mit der
// Kennung „plugins“ - ohne sie wird nichts geholt und die Leiste bleibt wie bisher.
import { api, featureOn } from '../api'
import { lang } from '../i18n'
import { makeT, PluginFactory, PluginModule, PluginView, registerLang } from '../sdk'
import { installHost } from './host'

export interface PluginInfo { key: string; version: string; api: number; on: boolean; label: Record<string, string>; web: string | null; langs: string[] }
export interface LoadedPlugin { info: PluginInfo; module: PluginModule; error?: string }

let loaded: LoadedPlugin[] = []
let started: Promise<void> | null = null

export const plugins = () => loaded
export const pluginViews = (): (PluginView & { plugin: string })[] => loaded.flatMap(p => (p.module.views ?? []).map(v => ({ ...v, plugin: p.info.key })))
// [feature:adminclean]
export const pluginSettingsPages = () => loaded.filter(p => p.module.settingsPages?.length).map(p => ({ plugin: p.info, pages: p.module.settingsPages! }))
// [/feature:adminclean]
export const pluginLabel = (p: PluginInfo) => p.label[lang] ?? p.label.de ?? p.key

/** Einmal nach der Anmeldung: Liste holen, Web-Teile laden. Fehler eines Plugins bleiben bei ihm (Konsole), der Rest läuft. */
export function loadPlugins(): Promise<void> {
  if (!featureOn('plugins')) return Promise.resolve()
  started ??= (async () => {
    installHost()
    let list: PluginInfo[] = []
    try { list = await api.get<PluginInfo[]>('/api/plugins') } catch { return }
    const result: LoadedPlugin[] = []
    for (const info of list.filter(p => p.on && p.web)) {
      try {
        if (lang !== 'de' && info.langs.includes(lang))
          registerLang(info.key, await fetch(`/plugins/${info.key}/lang/${lang}.json`).then(r => r.ok ? r.json() : {}))
        const mod = await import(/* @vite-ignore */ info.web!)
        const factory: PluginFactory | PluginModule = mod.default
        const module = typeof factory === 'function' ? factory({ key: info.key, version: info.version, t: makeT(info.key) }) : factory
        result.push({ info, module })
      } catch (e) {
        console.error(`Plugin ${info.key} nicht geladen`, e)
        result.push({ info, module: {}, error: String(e) })
      }
    }
    loaded = result
    window.dispatchEvent(new Event('dmoffice:plugins'))
  })()
  return started
}
