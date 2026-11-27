import { Button, makeStyles, mergeClasses, tokens } from '@fluentui/react-components'
import { Add16Regular, Dismiss12Regular, Folder16Regular } from '@fluentui/react-icons'
import { t } from '../i18n'

/**
 * Ordner-Tabs (Kennung foldertabs) wie in Directory Opus: je Fenster mehrere Ordner als Tabs über der Pfadleiste.
 * Neu mit „+“ (aktueller Ordner), per Rechtsklick „In neuem Tab öffnen“ oder Mittelklick auf einen Ordner; Schließen mit „×“ oder Mittelklick.
 */
const useStyles = makeStyles({
  strip: {
    display: 'flex', alignItems: 'flex-end', gap: '2px', padding: '4px 8px 0', minHeight: '30px', overflowX: 'auto', flexShrink: 0,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`, scrollbarWidth: 'none', '::-webkit-scrollbar': { display: 'none' },
  },
  tab: {
    display: 'flex', alignItems: 'center', gap: '6px', maxWidth: '200px', minWidth: 0, height: '26px', padding: '0 4px 0 10px', cursor: 'pointer',
    borderRadius: `${tokens.borderRadiusMedium} ${tokens.borderRadiusMedium} 0 0`, border: `1px solid transparent`, borderBottom: 'none',
    color: tokens.colorNeutralForeground2, fontSize: tokens.fontSizeBase200, userSelect: 'none', flexShrink: 0,
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
  },
  on: {
    color: tokens.colorNeutralForeground1, fontWeight: tokens.fontWeightSemibold, backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`, borderBottom: `1px solid ${tokens.colorNeutralBackground1}`, marginBottom: '-1px',
  },
  name: { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0 },
  close: { minWidth: '18px', width: '18px', height: '18px', padding: 0 },
})

/** Farbe externer Ordner (Seitenleiste und Tabs); Grün ist dem offenen Tresor vorbehalten */
export const EXT_COLOR = tokens.colorPaletteBlueBorderActive

export function FolderTabs({ labels, external, active, onPick, onClose, onNew }: {
  labels: string[]; active: number; onPick: (i: number) => void; onClose: (i: number) => void; onNew: () => void
  /** je Tab: externer Ordner (blau) */
  external?: boolean[]
}) {
  const s = useStyles()
  return (
    <div className={s.strip} role="tablist" aria-label={t('Ordner-Tabs')}>
      {labels.map((l, i) => (
        <div key={i} role="tab" aria-selected={i === active} className={mergeClasses(s.tab, i === active && s.on)} title={l}
          onClick={() => onPick(i)}
          onMouseDown={e => { if (e.button === 1) e.preventDefault() }}
          onAuxClick={e => { if (e.button === 1 && labels.length > 1) { e.preventDefault(); onClose(i) } }}>
          <Folder16Regular style={{ flexShrink: 0, color: external?.[i] ? EXT_COLOR : undefined }} />
          <span className={s.name}>{l}</span>
          {labels.length > 1 && <Button className={s.close} size="small" appearance="transparent" icon={<Dismiss12Regular />}
            aria-label={t('Tab schließen')} title={t('Tab schließen')} onClick={e => { e.stopPropagation(); onClose(i) }} />}
        </div>
      ))}
      <Button size="small" appearance="subtle" icon={<Add16Regular />} onClick={onNew} aria-label={t('Neuer Tab')} title={t('Neuer Tab mit diesem Ordner')} style={{ minWidth: 26, height: 26, marginBottom: 1 }} />
    </div>
  )
}
