import { defineConfig, type Plugin } from 'vite'
import react from '@vitejs/plugin-react'
import { createHash } from 'node:crypto'
import { readdirSync, readFileSync, statSync, writeFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { brotliCompressSync, constants as zlib, gzipSync } from 'node:zlib'

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
      // Auch Unterordner (public/pukki/...); Pfade immer mit „/“, versteckte Dateien und Ordner nicht
      const pub = (readdirSync(here('public'), { recursive: true }) as string[]).map(f => f.replaceAll('\\', '/'))
        .filter(f => !f.split('/').some(part => part.startsWith('.')) && statSync(here('public/' + f)).isFile())
      // Lizenzliste (third-party.json) nicht vorab laden: groß und selten geöffnet
      const files = [...Object.keys(bundle).filter(f => f !== 'index.html'), ...pub.filter(f => f !== 'third-party.json')].map(f => '/' + f).sort()
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

/**
 * Legt neben jede Text-Datei (js, css, json, svg, html) eine vorkomprimierte .br und .gz.
 * Der Server liefert sie aus, wenn der Browser das annimmt (Program.cs). Erst nach dem Schreiben
 * (closeBundle), damit sie nicht in der Vorab-Liste des Service Workers landen.
 */
function precompress(): Plugin {
  let outDir = ''
  return {
    name: 'dmoffice-precompress',
    apply: 'build',
    configResolved(c) { outDir = resolve(c.root, c.build.outDir) },
    closeBundle() {
      const files = (readdirSync(outDir, { recursive: true }) as string[]).map(f => resolve(outDir, f))
        .filter(f => /\.(js|css|json|svg|html)$/.test(f) && statSync(f).isFile() && statSync(f).size >= 1024)
      for (const f of files) {
        const raw = readFileSync(f)
        const br = brotliCompressSync(raw, { params: { [zlib.BROTLI_PARAM_QUALITY]: 11, [zlib.BROTLI_PARAM_SIZE_HINT]: raw.length } })
        const gz = gzipSync(raw, { level: 9 })
        if (br.length < raw.length * 0.9) writeFileSync(f + '.br', br)
        if (gz.length < raw.length * 0.9) writeFileSync(f + '.gz', gz)
      }
    },
  }
}

// Gebaut wird in den wwwroot des Servers; im Entwickeln leitet Vite /api und /dav dorthin weiter.
export default defineConfig({
  plugins: [react(), serviceWorker(), precompress()],
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
