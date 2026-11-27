#!/usr/bin/env node
// Siegel für Plugin-Ordner (docs/plugins-konzept.md 6.2, Gegenstück: DmOffice.Server/Plugins/PluginSeal.cs).
// SHA-256 jeder Datei außer certificate.json, sortiert, signiert mit ECDSA P-256 (SHA-256, DER).
//
//   node tools/plugins/seal.mjs keygen <ordner>          privaten Schlüssel (private.pem) und öffentlichen (public.txt, Base64) anlegen
//   node tools/plugins/seal.mjs seal <plugin-ordner> <private.pem>   certificate.json schreiben
//   node tools/plugins/seal.mjs verify <plugin-ordner> <public.txt>  prüfen (Rückgabe 0 = gültig)
//
// Der private Schlüssel gehört nie ins Repo. Den öffentlichen nimmt der Kern aus PluginSeal.BuiltIn oder DMOFFICE_PLUGIN_KEY.
import { createHash, createPrivateKey, createPublicKey, generateKeyPairSync, sign, verify } from 'node:crypto'
import { existsSync, mkdirSync, readdirSync, readFileSync, statSync, writeFileSync } from 'node:fs'
import { join, relative, sep } from 'node:path'

const HEAD = 'dmoffice-plugin-seal-1'
const CERT = 'certificate.json'

function files(dir, base = dir, out = {}) {
  for (const name of readdirSync(dir)) {
    const full = join(dir, name)
    if (statSync(full).isDirectory()) files(full, base, out)
    else {
      const rel = relative(base, full).split(sep).join('/')
      if (rel !== CERT) out[rel] = createHash('sha256').update(readFileSync(full)).digest('hex')
    }
  }
  return out
}

// Gleiche Sortierung wie StringComparer.Ordinal in C# (UTF-16-Codeeinheiten)
const ordinal = (a, b) => (a < b ? -1 : a > b ? 1 : 0)

function canonical(key, version, list) {
  let s = `${HEAD}\n${key}\n${version}\n`
  for (const p of Object.keys(list).sort(ordinal)) s += `${p} ${list[p].toLowerCase()}\n`
  return Buffer.from(s, 'utf8')
}

function manifest(dir) {
  const m = JSON.parse(readFileSync(join(dir, 'plugin.json'), 'utf8'))
  if (!m.key || !m.version) throw new Error('plugin.json ohne key oder version')
  return m
}

const [cmd, a, b] = process.argv.slice(2)
if (cmd === 'keygen' && a) {
  mkdirSync(a, { recursive: true })
  if (existsSync(join(a, 'private.pem'))) { console.error('private.pem gibt es schon - nichts überschrieben.'); process.exit(1) }
  const { privateKey, publicKey } = generateKeyPairSync('ec', { namedCurve: 'prime256v1' })
  writeFileSync(join(a, 'private.pem'), privateKey.export({ type: 'pkcs8', format: 'pem' }), { mode: 0o600 })
  writeFileSync(join(a, 'public.txt'), publicKey.export({ type: 'spki', format: 'der' }).toString('base64') + '\n')
  console.log('Schlüssel angelegt in ' + a + ' (private.pem geheim halten, public.txt ist der öffentliche Teil).')
} else if (cmd === 'seal' && a && b) {
  const m = manifest(a)
  const list = files(a)
  const sig = sign('sha256', canonical(m.key, m.version, list), { key: createPrivateKey(readFileSync(b)), dsaEncoding: 'der' })
  writeFileSync(join(a, CERT), JSON.stringify({ key: m.key, version: m.version, files: list, signature: sig.toString('base64') }, null, 2))
  console.log(`Gesiegelt: ${m.key} ${m.version}, ${Object.keys(list).length} Dateien.`)
} else if (cmd === 'verify' && a && b) {
  const m = manifest(a)
  const cert = JSON.parse(readFileSync(join(a, CERT), 'utf8'))
  const now = files(a)
  const listed = cert.files ?? {}
  const bad = [...Object.keys(now).filter(p => listed[p]?.toLowerCase() !== now[p]), ...Object.keys(listed).filter(p => !(p in now))]
  const key = createPublicKey({ key: Buffer.from(readFileSync(b, 'utf8').trim(), 'base64'), format: 'der', type: 'spki' })
  const ok = cert.key === m.key && cert.version === m.version && bad.length === 0
    && verify('sha256', canonical(cert.key, cert.version, listed), { key, dsaEncoding: 'der' }, Buffer.from(cert.signature, 'base64'))
  console.log(ok ? 'Siegel gültig.' : 'Siegel ungültig' + (bad.length ? ': ' + bad.join(', ') : '.'))
  process.exit(ok ? 0 : 1)
} else {
  console.error('Aufruf: seal.mjs keygen <ordner> | seal <plugin-ordner> <private.pem> | verify <plugin-ordner> <public.txt>')
  process.exit(2)
}
