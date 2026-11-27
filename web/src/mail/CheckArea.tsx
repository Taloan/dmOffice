import { Button, CounterBadge, Link, Popover, PopoverSurface, PopoverTrigger, Text, Tooltip, makeStyles, tokens } from '@fluentui/react-components'
import { DocumentBulletList20Regular, EyeOff20Regular, ShieldCheckmark20Regular, ShieldError20Regular } from '@fluentui/react-icons'
import { ReactNode } from 'react'
import { Message } from '../api'
import { useDmsFiled } from '../dms/MailToDms'
import { t } from '../i18n'
import { PgpBarView, pgpIntent, usePgpSignature } from './Pgp'

const useStyles = makeStyles({
  area: { display: 'flex', alignItems: 'center', gap: '2px', flexShrink: 0 },
  icon: { position: 'relative' },
  badge: { position: 'absolute', top: '-2px', right: '-4px', pointerEvents: 'none' },
  surface: { maxWidth: '380px', display: 'flex', flexDirection: 'column', gap: '8px' },
  rows: { display: 'grid', gridTemplateColumns: '1fr auto', columnGap: '16px', rowGap: '2px' },
  muted: { color: tokens.colorNeutralForeground3 },
})

const COLORS = {
  success: tokens.colorPaletteGreenForeground1,
  warning: tokens.colorPaletteMarigoldForeground1,
  error: tokens.colorPaletteRedForeground1,
  info: tokens.colorNeutralForeground2,
} as const

/** Ein Symbol im Prüfbereich: Klick öffnet die Einzelheiten */
function Check({ label, icon, color, count, children }: { label: string; icon: ReactNode; color?: string; count?: number; children: ReactNode }) {
  const s = useStyles()
  return (
    <Popover withArrow positioning="below-end">
      <PopoverTrigger disableButtonEnhancement>
        <Tooltip content={label} relationship="label">
          <span className={s.icon}>
            <Button appearance="subtle" icon={<span style={{ display: 'inline-flex', color }}>{icon}</span>} />
            {!!count && <CounterBadge className={s.badge} size="small" color="informative" count={count} />}
          </span>
        </Tooltip>
      </PopoverTrigger>
      <PopoverSurface className={s.surface}>{children}</PopoverSurface>
    </Popover>
  )
}

/**
 * Prüfbereich oben rechts im Kopf einer Mail (Kennung mailtrackers): kleine Symbole statt Leisten über dem Text -
 * Signatur, Tracker-Schutz, im DMS abgelegt. Einzelheiten per Klick. Warnungen (Phishing, Junk, Spam) bleiben Leisten.
 */
export function CheckArea({ message }: { message: Message }) {
  const s = useStyles()
  const pgp = usePgpSignature(message)
  const intent = message.pgp ? pgpIntent(pgp, message.from?.address) : null
  const filed = useDmsFiled(message.account, message.messageId)
  const tr = message.trackers
  return (
    <div className={s.area}>
      {intent && (
        <Check label={intent === 'success' ? t('Signatur geprüft') : t('Signatur')} color={COLORS[intent]}
          icon={intent === 'error' || intent === 'warning' ? <ShieldError20Regular /> : <ShieldCheckmark20Regular />}>
          <PgpBarView message={message} state={pgp} />
        </Check>
      )}
      {tr && (
        <Check label={t('Tracker-Schutz')} icon={<EyeOff20Regular />} count={tr.pixels + tr.links}
          color={tr.pixels + tr.links > 0 ? tokens.colorBrandForeground1 : tokens.colorNeutralForeground3}>
          <Text weight="semibold">{t('Tracker-Schutz')}</Text>
          <div className={s.rows}>
            <Text>{t('Zählpixel entfernt')}</Text><Text>{tr.pixels}</Text>
            <Text>{t('Links bereinigt')}</Text><Text>{tr.links}</Text>
          </div>
          <Text size={200} className={s.muted}>
            {tr.pixels + tr.links > 0
              ? t('Zählpixel melden dem Absender, ob und wann die Mail geöffnet wurde. dmOffice lädt sie nicht und entfernt aus Links Angaben zur Nachverfolgung, etwa utm_source.')
              : t('In dieser Mail wurden keine bekannten Zählpixel und keine Angaben zur Nachverfolgung in Links gefunden.')}
          </Text>
          <Text size={200} className={s.muted}>{t('Erkannt wird nach bekannten Mustern. Weiterleitungslinks von Versanddiensten bleiben unverändert.')}</Text>
        </Check>
      )}
      {filed > 0 && (
        <Check label={t('Im DMS abgelegt ({n})', { n: filed })} icon={<DocumentBulletList20Regular />} count={filed} color={tokens.colorNeutralForeground2}>
          <Text>{t('Diese Mail oder Anhänge daraus liegen im DMS ({n}).', { n: filed })}</Text>
          <Link onClick={() => { location.hash = '#/dms' }}>{t('DMS öffnen')}</Link>
        </Check>
      )}
    </div>
  )
}
