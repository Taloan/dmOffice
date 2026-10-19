import { defineConfig, type Plugin } from 'vite'
import react from '@vitejs/plugin-react'
import { createHash } from 'node:crypto'
import { readdirSync, readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'

const here = (p: string) => fileURLToPath(new URL(p, import.meta.url))

/**
 * Service Worker (installierbare App): nimmt sw/sw.js, setzt die Liste aller gebauten
 * Dateien und eine Versionskennung ein und legt das Ergebnis als /sw.js daneben.
 * Aendert sich eine Datei, aendert sich sw.js - der Browser bietet dann "Neu laden" an.
 */
function serviceWorker(): Plugin {
  return {
    name: 'dmoffice-sw',
    apply: 'build',
    generateBundle(_, bundle) {
      const pub = readdirSync(here('public')).filter(f => !f.startsWith('.'))
      const files = [...Object.keys(bundle).filter(f => f !== 'index.html'), ...pub].map(f => '/' + f).sort()
      const hash = createHash('sha256')
      for (const [name, item] of Object.entries(bundle)) hash.update(name).update(item.type === 'chunk' ? item.code : item.source)
      for (const f of pub) hash.update(f).update(readFileSync(here('public/' + f)))
      const version = hash.digest('hex').slice(0, 12)
      const code = readFileSync(here('sw/sw.js'), 'utf8')
        .replace("'__VERSION__'", JSON.stringify(version))
        .replace("'__PRECACHE__'", JSON.stringify(files))
      this.emitFile({ type: 'asset', fileName: 'sw.js', source: code })
    },
  }
}

// Gebaut wird in den wwwroot des Servers; im Entwickeln leitet Vite /api und /dav dorthin weiter.
export default defineConfig({
  plugins: [react(), serviceWorker()],
  build: {
    outDir: '../DmOffice.Server/wwwroot',
    emptyOutDir: true,
    chunkSizeWarningLimit: 2000,
  },
  server: {
    port: 5173,
    proxy: {
      '/api': 'http://127.0.0.1:5099',
      '/dav': 'http://127.0.0.1:5099',
    },
  },
})
