import {
  Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Link, Menu, MenuDivider, MenuItem, MenuList, MenuPopover, MenuTrigger, Text, Tooltip, tokens,
} from '@fluentui/react-components'
import { Book20Regular, Globe20Regular, Info20Regular, QuestionCircle20Regular } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { useApp } from '../App'
import { featureOn } from '../api'
import { lang, t } from '../i18n'
import { copyText } from './browser'
import { brandLogo } from '../theme'
import { currentPrefs } from '../prefs'
import { OpenSourceDialog } from './OpenSource'

export const WEBSITE = 'https://dmoffice.de'
export const GITHUB = 'https://github.com/Taloan/dmOffice'
const MANUAL = GITHUB + '/blob/master/docs/' + (lang === 'de' ? 'handbuch.md' : 'manual.md')

const open = (url: string) => { window.open(url, '_blank', 'noopener,noreferrer') }

/** Hilfe in der Kopfzeile: Webseite, Handbuch, GitHub und ein Infofenster. */
export function HelpMenu({ className, release, build, extra }: { className?: string; release: string; build: string; extra?: string[] }) {
  const [about, setAbout] = useState(false)
  // F1 öffnet überall das Handbuch (Kennung f1help)
  useEffect(() => {
    if (!featureOn('f1help')) return
    const onKey = (e: KeyboardEvent) => { if (e.key === 'F1' && !e.ctrlKey && !e.altKey && !e.metaKey && !e.shiftKey) { e.preventDefault(); open(MANUAL) } }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [])
  return (
    <>
      <Menu>
        <MenuTrigger disableButtonEnhancement>
          <Tooltip content={t('Hilfe')} relationship="label">
            <Button appearance="transparent" className={className} icon={<QuestionCircle20Regular />} />
          </Tooltip>
        </MenuTrigger>
        <MenuPopover>
          <MenuList>
            <MenuItem icon={<Globe20Regular />} onClick={() => open(WEBSITE)}>{t('Webseite von dmOffice')}</MenuItem>
            <MenuItem icon={<Book20Regular />} secondaryContent={featureOn('f1help') ? 'F1' : undefined} onClick={() => open(MANUAL)}>{t('Handbuch')}</MenuItem>
            <MenuItem icon={<Globe20Regular />} onClick={() => open(GITHUB)}>{t('dmOffice auf GitHub')}</MenuItem>
            <MenuDivider />
            <MenuItem icon={<Info20Regular />} onClick={() => setAbout(true)}>{t('Über dmOffice')}</MenuItem>
          </MenuList>
        </MenuPopover>
      </Menu>
      {about && <AboutDialog release={release} build={build} extra={extra ?? []} onClose={() => setAbout(false)} />}
    </>
  )
}

function AboutDialog({ release, build, extra, onClose }: { release: string; build: string; extra: string[]; onClose: () => void }) {
  const { notify } = useApp()
  const [oss, setOss] = useState(false)
  const text = [release && `v${release}`, extra.length > 0 && `+ ${extra.join(', ')}`, build && `(${t('Build')} ${build})`].filter(Boolean).join(' ')
  return (
    <>
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose() }}>
      <DialogSurface style={{ maxWidth: 440 }}>
        <DialogBody>
          <DialogTitle>{t('Über dmOffice')}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
              <img src={brandLogo(currentPrefs.accent)} width={44} height={44} alt="" />
              <div>
                <Text weight="semibold" size={500} block>dmOffice {release && <span style={{ fontWeight: 400 }}>v{release}</span>}</Text>
                <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('Mail, Kalender, Kontakte, Aufgaben, Notizen und Dateien auf deinem eigenen Server.')}</Text>
              </div>
            </div>
            {extra.length > 0 && <Text size={200}>{t('Zusätzlich freigeschaltet: {list}', { list: extra.join(', ') })}</Text>}
            {build && (
              <Text size={200}>
                {t('Build')}: <span style={{ fontFamily: tokens.fontFamilyMonospace }}>{build}</span>{' '}
                <Link onClick={() => { copyText(text).then(ok => ok && notify(t('Version kopiert'))) }}>{t('Kopieren')}</Link>
              </Text>
            )}
            <Text size={200}>{t('Freie Software unter der GNU Affero General Public License (AGPL-3.0).')}</Text>
            <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
              <Link href={WEBSITE} target="_blank" rel="noopener noreferrer">{WEBSITE.replace('https://', '')}</Link>
              <Link href={GITHUB} target="_blank" rel="noopener noreferrer">{GITHUB.replace('https://', '')}</Link>
              <Link onClick={() => setOss(true)}>{t('Open-Source-Bibliotheken')}</Link>
            </div>
          </DialogContent>
          <DialogActions>
            <Button appearance="primary" onClick={onClose}>{t('Schließen')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
    {oss && <OpenSourceDialog onClose={() => setOss(false)} />}
    </>
  )
}
