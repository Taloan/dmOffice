// Zwischenablage der Dateiablage, gemeinsam für „Meine Dateien“ (mount null) und externe Ordner (mount = Kennung):
// Ausschneiden oder Kopieren auf der einen Seite, Einfügen auf der anderen - auch über die Grenze hinweg.
import { useSyncExternalStore } from 'react'

export type FileClip = { mount: string | null; paths: string[]; cut: boolean }

let current: FileClip | null = null
const listeners = new Set<() => void>()

export function setFileClip(c: FileClip | null) { current = c; listeners.forEach(l => l()) }

export function useFileClip(): [FileClip | null, (c: FileClip | null) => void] {
  const c = useSyncExternalStore(l => { listeners.add(l); return () => { listeners.delete(l) } }, () => current)
  return [c, setFileClip]
}
