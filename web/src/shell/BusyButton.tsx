import { Button, ButtonProps } from '@fluentui/react-components'
import { useRef, useState } from 'react'

/**
 * Knopf für Speichern und Anlegen: bis die Anfrage fertig ist, gesperrt - ein Doppelklick legt nichts doppelt an.
 * Die Sperre greift sofort (Ref), nicht erst nach dem nächsten Zeichnen.
 */
export function BusyButton({ onClick, disabled, ...props }: Pick<ButtonProps, 'appearance' | 'icon' | 'size' | 'children' | 'style' | 'title' | 'className'> & { disabled?: boolean; onClick: () => unknown }) {
  const running = useRef(false)
  const [busy, setBusy] = useState(false)
  return (
    <Button {...props} disabled={disabled || busy} onClick={async () => {
      if (running.current) return
      running.current = true
      setBusy(true)
      try { await onClick() } finally { running.current = false; setBusy(false) }
    }} />
  )
}
