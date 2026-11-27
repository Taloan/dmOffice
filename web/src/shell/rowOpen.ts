// Doppelklick auf eine Tabellenzeile öffnet sie (Kennung rowdblopen, ab 2.2): löst die vorhandene Öffnen-Aktion der Zeile aus
// (Menüband „… öffnen“, „Bearbeiten“ o. ä.) und verhindert, dass der Doppelklick Text markiert.
import type React from 'react'
import { featureOn } from '../api'

/** Bedienelemente in der Zeile (Kästchen, Knöpfe, Eingabefelder): Doppelklick dort bleibt deren Sache. */
const CONTROL = 'button, input, select, textarea, a, [role="button"], [role="checkbox"], [role="combobox"], [contenteditable="true"]'
const onControl = (e: React.MouseEvent) => e.target instanceof Element && !!e.target.closest(CONTROL)

/**
 * Ereignisse für eine Zeile: Doppelklick ruft open auf. onMouseDown: vorhandener Handler der Zeile, wird mit aufgerufen.
 * Ohne Kennung: keine Ereignisse (bzw. nur der vorhandene onMouseDown).
 */
export function dblOpen(open: () => void, onMouseDown?: (e: React.MouseEvent<HTMLElement>) => void) {
  if (!featureOn('rowdblopen')) return onMouseDown ? { onMouseDown } : {}
  return {
    onDoubleClick: (e: React.MouseEvent<HTMLElement>) => { if (onControl(e)) return; e.preventDefault(); open() },
    onMouseDown: (e: React.MouseEvent<HTMLElement>) => { if (e.detail > 1 && !onControl(e)) e.preventDefault(); onMouseDown?.(e) },
  }
}
