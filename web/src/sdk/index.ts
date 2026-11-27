// @dmoffice/sdk - Kern-Schnittstelle für die Web-Teile der Plugins (docs/plugins-konzept.md 4.3, 5).
// Plugins importieren nur hieraus, aus react und aus @fluentui/react-components; zur Laufzeit stellt host.ts alle drei als
// gemeinsame Instanzen bereit. Stufe 1 (SDK_LEVEL): innerhalb einer Stufe wird nur hinzugefügt.
import type { ComponentType, ReactNode } from 'react'
import { api, ApiError, q, featureOn } from '../api'
import { lang, locale, t as coreT } from '../i18n'
import { useApp } from '../App'

export const SDK_LEVEL = 1

export { api, ApiError, q, featureOn, lang, locale, useApp }
/** Rückfrage als eigener Dialog statt confirm(): der Browser beantwortet confirm() stumm mit „Nein“, sobald man dort
 *  „keine weiteren Dialoge“ gewählt hat. Leerer Titel: nur der Fragetext. */
export { askConfirm } from '../shell/context'

/** Tabelle wie im Explorer (Kopf sortiert, Breite und Reihenfolge ziehbar, Rechtsklick wählt Spalten) - wie in der Verwaltung. */
export { AdminTable as ExplorerTable, pickCol, selectCol } from '../admin/AdminTable'
/** Aussehen von Tabellenkopf und -rahmen für eigene Raster-Tabellen (in makeStyles einsetzen) - gleich wie ExplorerTable. */
export { tableFrameLook, tableHeadLook } from '../shell/GridColumns'
export type { AdminCol as ExplorerCol } from '../admin/AdminTable'

/**
 * Standardaufbau eines Bereichs (wie DMS, Finanzen): oben das Menüband mit „Start“ und „Ansicht“ (RibbonHeader, RibbonButton,
 * RibbonDivider), links die Abschnittsleiste mit der Struktur des Bereichs (SidePane bringt Trennlinie zum Inhalt mit, ein-/ausblendbar über useSidePane; Zeilen mit
 * useNavStyles und pressable). Plugins bauen ihre Seiten genauso – keine eigenen Kopfzeilen.
 */
export { RibbonButton, RibbonDivider, RibbonHeader, useSidePane } from '../shell/Ribbon'
// [feature:adminclean]
/**
 * W9: Aufbau einer Seite in den persönlichen Einstellungen (Zahnrad) wie die Seiten des Kerns: Abschnitte mit Überschrift
 * (SettingsSection), lange Seiten mit Reitern je Abschnitt (SettingsTabs, Teil in eigener Komponente: SettingsTabPart),
 * Aktionen ins Menüband der Einstellungen (SettingsActions). Die Überschrift der Seite setzen die Einstellungen selbst.
 */
export { Section as SettingsSection, SectionTabs as SettingsTabs, TabPart as SettingsTabPart } from '../settings/parts'
export { RibbonActions as SettingsActions } from '../shell/Ribbon'
// [/feature:adminclean]
export { SidePane, useIsMobile } from '../shell/mobile'
export { pressable, useNavStyles } from '../shell/nav'
/** Dateien aus der eigenen Ablage wählen (AblagePicker) und ihren Inhalt holen (ablageBlob) - etwa für Bilder einer Website. */
export { AblagePicker, ablageBlob } from '../shell/AblagePicker'
export type { AblageItem } from '../shell/AblagePicker'

/** W1: Bereich in der Leiste. id ist zugleich die Adresse (#/id); label in beiden Sprachen oder schon übersetzt. */
export interface PluginView {
  id: string
  label: string
  icon?: ComponentType
  component: ComponentType
}

/** Was ein Plugin per Default-Export liefert. Alles optional. */
export interface PluginModule {
  /** W1 */
  views?: PluginView[]
  /** W5: Erklärung je Bereich des Datenexports (Kennung aus IExportArea.Key) */
  exportHints?: Record<string, string>
  // [feature:adminclean]
  /** W9: Seiten in den persönlichen Einstellungen (Zahnrad), als eigener Bereich des Plugins */
  settingsPages?: PluginSettingsPage[]
  // [/feature:adminclean]
}

// [feature:adminclean]
/** W9: Seite in den Einstellungen. id ist zugleich die Adresse (#/settings/id), am besten mit dem Plugin-Schlüssel vorn.
 *  allowed: nur zeigen, wenn das Plugin es erlaubt (etwa nur mit Recht zum Bearbeiten); wird beim Öffnen der Einstellungen geprüft. */
export interface PluginSettingsPage {
  id: string
  label: string
  /** Suchwörter (in beiden Sprachen) */
  words?: string
  component: ComponentType
  allowed?: () => boolean | Promise<boolean>
}
// [/feature:adminclean]


/** Plugin-Modul anlegen: bekommt die Übersetzungsfunktion des Plugins (W6) und gibt die Beiträge zurück. */
export type PluginFactory = (ctx: PluginContext) => PluginModule

export interface PluginContext {
  key: string
  version: string
  /** Text in der Sprache der Oberfläche: Plugin-Sprachdatei lang/<code>.json, sonst Kern, sonst der deutsche Text. */
  t: (de: string, vars?: Record<string, string | number>) => string
}

/** Sprachdateien der Plugins: Schlüssel → { deutscher Text → Übersetzung } */
const dicts = new Map<string, Record<string, string>>()

export function registerLang(key: string, dict: Record<string, string>) { dicts.set(key, dict) }

export function makeT(key: string) {
  return (de: string, vars?: Record<string, string | number>) => {
    const own = lang === 'de' ? undefined : dicts.get(key)?.[de]
    // Platzhalter: in der eigenen Übersetzung hier, sonst setzt sie der Kern ein (auch im deutschen Text)
    if (!own) return coreT(de, vars)
    let s = own
    if (vars) for (const [k, v] of Object.entries(vars)) s = s.split(`{${k}}`).join(String(v))
    return s
  }
}

export type { ReactNode }
