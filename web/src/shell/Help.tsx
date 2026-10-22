import {
  Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Link, Menu, MenuDivider, MenuItem, MenuList, MenuPopover, MenuTrigger, Text, Tooltip, tokens,
} from '@fluentui/react-components'
import { Book20Regular, Globe20Regular, Info20Regular, QuestionCircle20Regular } from '@fluentui/react-icons'
import { useState } from 'react'
import { useApp } from '../App'
import { t } from '../i18n'
import { copyText } from './browser'

export const WEBSITE = 'https://dmoffice.de'
export const GITHUB = 'https://github.com/Taloan/dmOffice'
const MANUAL = GITHUB + '/blob/master/docs/manual.md'

const open = (url: string) => { window.open(url, '_blank', 'noopener,noreferrer') }

/** Hilfe in der Kopfzeile: Webseite, Handbuch, GitHub und ein Infofenster. */
export function HelpMenu({ className, release, build }: { className?: string; release: string; build: string }) {
  const [about, setAbout] = useState(false)
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
            <MenuItem icon={<Book20Regular />} onClick={() => open(MANUAL)}>{t('Handbuch')}</MenuItem>
            <MenuItem icon={<Globe20Regular />} onClick={() => open(GITHUB)}>{t('dmOffice auf GitHub')}</MenuItem>
            <MenuDivider />
            <MenuItem icon={<Info20Regular />} onClick={() => setAbout(true)}>{t('Über dmOffice')}</MenuItem>
          </MenuList>
        </MenuPopover>
      </Menu>
      {about && <AboutDialog release={release} build={build} onClose={() => setAbout(false)} />}
    </>
  )
}

function AboutDialog({ release, build, onClose }: { release: string; build: string; onClose: () => void }) {
  const { notify } = useApp()
  const text = [release && `v${release}`, build && `(${t('Build')} ${build})`].filter(Boolean).join(' ')
  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose() }}>
      <DialogSurface style={{ maxWidth: 440 }}>
        <DialogBody>
          <DialogTitle>{t('Über dmOffice')}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
              <img src="/favicon.svg" width={44} height={44} alt="" />
              <div>
                <Text weight="semibold" size={500} block>dmOffice {release && <span style={{ fontWeight: 400 }}>v{release}</span>}</Text>
                <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('Mail, Kalender, Kontakte, Aufgaben, Notizen und Dateien auf deinem eigenen Server.')}</Text>
              </div>
            </div>
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
              <Link href={GITHUB + '/blob/master/THIRD-PARTY-NOTICES.md'} target="_blank" rel="noopener noreferrer">{t('Lizenzen verwendeter Bibliotheken')}</Link>
            </div>
          </DialogContent>
          <DialogActions>
            <Button appearance="primary" onClick={onClose}>{t('Schließen')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
