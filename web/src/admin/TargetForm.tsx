import { Field, MessageBar, MessageBarBody, Radio, RadioGroup, Text, tokens } from '@fluentui/react-components'
import { PlugConnected20Regular } from '@fluentui/react-icons'
import { t } from '../i18n'
import { RibbonActions, RibbonButton } from '../shell/Ribbon'
import { Target, TargetFields, TargetValues } from './TargetFields'

// Kennung adminclean: ein gemeinsamer Baustein für das Ziel (Storage Extender, Core-Backup, Files-Backup). Mit „mode“
// steht zuerst nur „Ziel: wie Storage Extender / eigenes“, das ganze Formular erscheint erst bei „eigenes“.
// „Speichern und testen“ liegt im Menüband.

export interface TargetFormProps {
  /** nur bei Backup: dasselbe Ziel wie der Storage Extender oder ein eigenes */
  mode?: 'same' | 'own'
  onMode?: (m: 'same' | 'own') => void
  /** Ort des Storage-Extender-Ziels; null = dort ist keins eingerichtet */
  samePlace?: string | null
  target: Target
  onTarget: (x: Target) => void
  v: TargetValues
  onChange: (k: keyof TargetValues, value: string) => void
  hasSecret: boolean
  hasWebDavPassword: boolean
  kindLocked?: boolean
  onTest: () => void
  busy: boolean
  tested: boolean
  /** Beschriftung der Schaltfläche im Menüband */
  testLabel?: string
}

export function TargetForm(p: TargetFormProps) {
  const own = p.mode == null || p.mode === 'own' || p.samePlace == null
  return <>
    {p.mode != null && <Field label={t('Ziel')}>
      <RadioGroup layout="horizontal" value={own ? 'own' : 'same'} onChange={(_, d) => p.onMode?.(d.value as 'same' | 'own')}>
        <Radio value="same" disabled={p.samePlace == null} label={t('wie Storage Extender')} />
        <Radio value="own" label={t('eigenes')} />
      </RadioGroup>
      <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
        {own ? '' : p.samePlace ?? ''}{p.samePlace == null ? t('Im Storage Extender ist kein Ziel eingerichtet.') : ''}
      </Text>
    </Field>}
    {own && <>
      <TargetFields target={p.target} onTarget={p.onTarget} v={p.v} onChange={p.onChange} hasSecret={p.hasSecret} hasWebDavPassword={p.hasWebDavPassword} kindLocked={p.kindLocked} />
      <RibbonActions>
        <RibbonButton icon={<PlugConnected20Regular />} primary disabled={p.busy} label={p.busy ? t('Prüfe …') : p.testLabel ?? t('Speichern und testen')} onClick={p.onTest} />
      </RibbonActions>
      {p.tested && <MessageBar intent="success"><MessageBarBody>{t('Verbindung geprüft: dmOffice kann dort schreiben, lesen und löschen.')}</MessageBarBody></MessageBar>}
    </>}
  </>
}
