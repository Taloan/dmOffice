import { Button, MessageBar, MessageBarActions, MessageBarBody, MessageBarTitle, makeStyles } from '@fluentui/react-components'
import { PhishFinding } from '../api'
import { t } from '../i18n'

const useStyles = makeStyles({
  list: { margin: '4px 0 0', paddingLeft: '18px' },
  advice: { marginTop: '4px' },
})

/** Ein Satz je Merkmal - die Domains kommen vom Server, der Wortlaut von hier */
export function phishText(f: PhishFinding): string {
  switch (f.kind) {
    case 'auth':
      return f.detail === 'dmarc'
        ? t('Die Absenderadresse ist vermutlich gefälscht: Der Mailserver konnte nicht bestätigen, dass die Mail wirklich von {domain} kommt.', { domain: f.domain ?? '' })
        : t('Die Mail kam nicht über einen Server, den {domain} für seine Mails angibt.', { domain: f.domain ?? '' })
    case 'link':
      return t('Ein Link zeigt „{shown}“ an, führt aber zu „{target}“.', { shown: f.shown ?? '', target: f.target ?? '' })
    case 'lookalike':
      if (f.detail === 'mixed') return t('Die Adresse {domain} mischt Buchstaben aus verschiedenen Alphabeten. So lassen sich bekannte Namen täuschend echt nachbauen.', { domain: f.domain ?? '' })
      return f.detail === 'sender'
        ? t('Die Absenderadresse gehört zu {domain}. Das sieht aus wie {like}, ist es aber nicht.', { domain: f.domain ?? '', like: f.like ?? '' })
        : t('Ein Link führt zu {domain}. Das sieht aus wie {like}, ist es aber nicht.', { domain: f.domain ?? '', like: f.like ?? '' })
    case 'newsender':
      return t('Von {sender} kam bisher keine Mail, und diese enthält einen Link zum Anmelden oder Bestätigen von Daten ({target}).', { sender: f.domain ?? '', target: f.target ?? '' })
  }
}

/** Warnung bei einem starken Merkmal oder zwei schwachen, sonst ein Hinweis */
export function phishWarns(found: PhishFinding[]) {
  return found.some(f => f.strong) || found.length >= 2
}

/** Warnbalken ueber dem Inhalt der Mail. Nur ein Hinweis - die Mail wird weder verschoben noch gesperrt. */
export function PhishBar({ found, className, onJunk }: { found: PhishFinding[]; className?: string; onJunk?: () => void }) {
  const s = useStyles()
  if (found.length === 0) return null
  const warn = phishWarns(found)
  return (
    <MessageBar intent={warn ? 'warning' : 'info'} className={className} layout="multiline">
      <MessageBarBody>
        <MessageBarTitle>{warn ? t('Vorsicht: Diese Mail könnte ein Betrugsversuch sein.') : t('Bei dieser Mail ist etwas ungewöhnlich.')}</MessageBarTitle>
        <ul className={s.list}>{found.map((f, i) => <li key={i}>{phishText(f)}</li>)}</ul>
        <div className={s.advice}>{t('Im Zweifel keinen Link anklicken und keine Zugangsdaten eingeben. Die Website besser selbst im Browser aufrufen.')}</div>
      </MessageBarBody>
      {onJunk && <MessageBarActions><Button size="small" onClick={onJunk}>{t('Junk')}</Button></MessageBarActions>}
    </MessageBar>
  )
}
