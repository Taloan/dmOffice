// Gemeinsame Instanzen für die zur Laufzeit geladenen Web-Teile der Plugins (docs/plugins-konzept.md 4.3): React, React DOM,
// JSX-Laufzeit, Fluent UI und @dmoffice/sdk. web/build-plugins.mjs ersetzt diese Importe im Plugin durch Zugriffe auf
// window.__dmoffice - so gibt es React und Fluent (samt Griffel-Stilen und Kontext) nur einmal.
import * as react from 'react'
import * as reactDom from 'react-dom'
import * as jsxRuntime from 'react/jsx-runtime'
import * as fluent from '@fluentui/react-components'
import * as griffel from '@griffel/react'
import * as sdk from '../sdk'

declare global {
  interface Window { __dmoffice?: Record<string, unknown> }
}

export function installHost() {
  window.__dmoffice = {
    react, 'react-dom': reactDom, 'react/jsx-runtime': jsxRuntime, '@fluentui/react-components': fluent, '@griffel/react': griffel, '@dmoffice/sdk': sdk,
  }
}
