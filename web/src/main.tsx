// Sprache zuerst festlegen - alles Weitere uebersetzt beim Zeichnen
import './i18n'
import { createRoot } from 'react-dom/client'
import { FluentProvider, Spinner } from '@fluentui/react-components'
import { lazy, Suspense } from 'react'
import { App } from './App'
import './global.css'
import { registerServiceWorker } from './pwa'
import { themesFor, useIsDark } from './theme'

// Oeffentlicher Link (/s/{token}): eigene Seite ohne Anmeldung
const PublicShare = lazy(() => import('./files/PublicShare'))
const shared = /^\/s\/([A-Za-z0-9_-]{16,64})\/?$/.exec(location.pathname)

function PublicPage({ token }: { token: string }) {
  const dark = useIsDark()
  const th = themesFor(undefined)
  return (
    <FluentProvider theme={dark ? th.dark : th.light} style={{ height: '100%' }}>
      <Suspense fallback={<Spinner style={{ marginTop: 80 }} />}><PublicShare token={token} /></Suspense>
    </FluentProvider>
  )
}

if (!shared) registerServiceWorker()

createRoot(document.getElementById('root')!).render(shared ? <PublicPage token={shared[1]} /> : <App />)
