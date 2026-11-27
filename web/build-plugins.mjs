#!/usr/bin/env node
// Web-Teile der Plugins bauen (docs/plugins-konzept.md 4.3): jedes plugins/<key>/web/src/index.ts(x) einzeln als ES-Modul nach
// plugins/<key>/web/entry.js. react, react-dom, react/jsx-runtime, @fluentui/react-components und @dmoffice/sdk kommen nicht ins
// Plugin, sondern zur Laufzeit aus dem Kern (window.__dmoffice, web/src/plugins/host.ts) - so gibt es sie nur einmal.
// Der Kern-Build kennt keine Plugins.
//
//   node build-plugins.mjs [--root <ordner mit Plugin-Ordnern>] [schlüssel …]
import { build } from 'vite'
import react from '@vitejs/plugin-react'
import { existsSync, readdirSync, readFileSync } from 'node:fs'
import { join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const here = fileURLToPath(new URL('.', import.meta.url))
const args = process.argv.slice(2)
const rootAt = args.indexOf('--root')
const root = resolve(rootAt >= 0 ? args[rootAt + 1] : join(here, '..', 'plugins'))
const only = args.filter((a, i) => !a.startsWith('--') && (rootAt < 0 || i !== rootAt + 1))

/** Gemeinsame Module und ihre Namen (aus dem echten Paket bzw. aus den Exporten des SDK). */
// @griffel/react: Fluent-Symbole bringen eigene Stil-Aufrufe mit - dieselbe Griffel-Instanz wie Fluent
const SHARED = ['react', 'react-dom', 'react/jsx-runtime', '@fluentui/react-components', '@griffel/react', '@dmoffice/sdk']

async function namesOf(id) {
  if (id === '@dmoffice/sdk') {
    const src = readFileSync(join(here, 'src', 'sdk', 'index.ts'), 'utf8')
    const names = new Set()
    for (const m of src.matchAll(/export\s+(?:async\s+)?(?:const|function|class|let)\s+([A-Za-z_$][\w$]*)/g)) names.add(m[1])
    for (const m of src.matchAll(/export\s*\{([^}]*)\}/g))
      for (const part of m[1].split(',')) { const n = part.trim().split(/\s+as\s+/).pop(); if (n && !/^type\s/.test(part.trim())) names.add(n) }
    return [...names]
  }
  const mod = await import(id)
  return Object.keys(mod).filter(k => k !== 'default' && /^[A-Za-z_$][\w$]*$/.test(k))
}

function sharedModules(names) {
  const PREFIX = '\0dmoffice-shared:'
  return {
    name: 'dmoffice-shared',
    enforce: 'pre',
    resolveId(id) { return SHARED.includes(id) ? PREFIX + id : null },
    load(id) {
      if (!id.startsWith(PREFIX)) return null
      const name = id.slice(PREFIX.length)
      const m = `globalThis.__dmoffice[${JSON.stringify(name)}]`
      // Ohne Nebenwirkung: ungenutzte Namen (bei Fluent mehrere hundert) fallen beim Bau weg
      return {
        moduleSideEffects: false,
        code: `const m = ${m};\nif (!m) throw new Error('dmOffice: ${name} fehlt (Plugin außerhalb von dmOffice geladen?)');\n` +
          `export default (m.default ?? m);\n` + names[name].map(n => `export const ${n} = m.${n};`).join('\n'),
      }
    },
  }
}

if (!existsSync(root)) { console.log('Keine Plugins (' + root + ').'); process.exit(0) }
const names = Object.fromEntries(await Promise.all(SHARED.map(async id => [id, await namesOf(id)])))
let count = 0
for (const key of readdirSync(root).sort()) {
  if (only.length && !only.includes(key)) continue
  const src = ['index.tsx', 'index.ts'].map(f => join(root, key, 'web', 'src', f)).find(existsSync)
  if (!src) continue
  await build({
    configFile: false,
    logLevel: 'warn',
    plugins: [sharedModules(names), react()],
    define: { 'process.env.NODE_ENV': '"production"' },
    root: join(root, key),
    // Symbole (Fluent-Icons) bringt jedes Plugin selbst mit - nur die genutzten, aus den Paketen des Kerns
    resolve: { alias: { '@fluentui/react-icons': join(here, 'node_modules', '@fluentui', 'react-icons') } },
    publicDir: false,
    build: {
      outDir: join(root, key, 'web'),
      emptyOutDir: false,
      lib: { entry: src, formats: ['es'], fileName: () => 'entry.js' },
      minify: true,
      // Lesen aus den geteilten Modulen (window.__dmoffice) hat keine Nebenwirkung
      rollupOptions: { treeshake: { propertyReadSideEffects: false } },
    },
  })
  console.log(`Plugin ${key}: web/entry.js gebaut.`)
  count++
}
if (!count) console.log('Kein Plugin mit web/src/index.ts(x).')
