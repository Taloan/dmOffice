// Sprache zuerst festlegen - alles Weitere uebersetzt beim Zeichnen
import './i18n'
import { createRoot } from 'react-dom/client'
import { App } from './App'
import './global.css'
import { registerServiceWorker } from './pwa'

registerServiceWorker()

createRoot(document.getElementById('root')!).render(<App />)
