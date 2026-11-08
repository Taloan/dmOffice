import { makeStyles, mergeClasses, Text, tokens } from '@fluentui/react-components'
import { api } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { Hint, Section } from '../settings/parts'
import { PUKKI_FIGURES, PukkiFigure, pukkiFigureImage, pukkiFigureName, setPukkiFigure, usePukkiFigure } from './UserAvatar'

const useStyles = makeStyles({
  row: { display: 'flex', gap: '12px', flexWrap: 'wrap' },
  choice: {
    display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '6px', padding: '8px', minWidth: '88px', cursor: 'pointer',
    border: `2px solid transparent`, borderRadius: tokens.borderRadiusLarge, backgroundColor: 'transparent', color: 'inherit', font: 'inherit',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
  },
  chosen: { border: `2px solid ${tokens.colorBrandStroke1}`, backgroundColor: tokens.colorBrandBackground2 },
  img: { width: '72px', height: '72px', borderRadius: '50%' },
})

/** Einstellungen -> Chat: als welche Figur Pukki erscheint - Panda, Walross, Bär oder Zauberer. */
export function PukkiFigureChoice() {
  const s = useStyles()
  const { fail } = useApp()
  const current = usePukkiFigure()
  const choose = async (f: PukkiFigure) => {
    if (f === current) return
    const before = current
    setPukkiFigure(f)
    try { await api.put('/api/chat/pukki/figure', { figure: f }) } catch (e) { setPukkiFigure(before); fail(e) }
  }
  return (
    <Section title={t('So sieht Pukki aus')}>
      <div className={s.row} role="radiogroup" aria-label={t('So sieht Pukki aus')}>
        {PUKKI_FIGURES.map(f => (
          <button key={f} type="button" role="radio" aria-checked={f === current} className={mergeClasses(s.choice, f === current && s.chosen)} onClick={() => choose(f)}>
            <img src={pukkiFigureImage(f)} alt="" className={s.img} />
            <Text weight={f === current ? 'semibold' : 'regular'}>{pukkiFigureName(f)}</Text>
          </button>
        ))}
      </div>
      <Hint>{t('Nur du siehst Pukki so. Jeder sucht sich seine eigene Figur aus.')}</Hint>
    </Section>
  )
}
