import {
  Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Input, Link, Spinner, Text, makeStyles, tokens,
} from '@fluentui/react-components'
import { ChevronDown16Regular, ChevronRight16Regular, Search20Regular } from '@fluentui/react-icons'
import { useEffect, useMemo, useState } from 'react'
import { lang, t } from '../i18n'

/** Aufbau von /third-party.json (tools/lizenzen/erzeugen.mjs). */
interface Entry {
  name: string; version?: string; license: string; url?: string; author?: string; copyright?: string; packages?: string[]; texts?: string[]; note?: Record<string, string>
}
interface List { sections: { id: string; entries: Entry[] }[]; texts: Record<string, string> }

const TITLES: Record<string, string> = {
  web: 'Weboberfläche', server: 'Server', app: 'Android-App', image: 'Programme im Docker-Image',
  services: 'Angebundene Dienste', specs: 'Formate und Spezifikationen', own: 'Eigene Bestandteile',
}
const NOTES: Record<string, string> = {
  app: 'Außerdem enthält die App die Google-Play-Dienste, Firebase Cloud Messaging und ML Kit; diese stehen unter den Nutzungsbedingungen von Google.',
  image: 'Als eigene Programme aufgerufen; die Lizenztexte der Pakete liegen im Image unter /usr/share/doc.',
  services: 'Optional, eigene Container; nicht Teil von dmOffice.',
  specs: 'Eigenständig nach der veröffentlichten Dokumentation umgesetzt.',
}

const useStyles = makeStyles({
  list: { display: 'flex', flexDirection: 'column', gap: '14px', maxHeight: '62vh', overflowY: 'auto', paddingRight: '4px' },
  row: {
    display: 'grid', gridTemplateColumns: '16px minmax(0, 1fr) auto', columnGap: '6px', alignItems: 'baseline', width: '100%',
    padding: '4px 2px', border: 'none', background: 'none', textAlign: 'left', cursor: 'pointer', color: 'inherit', font: 'inherit',
    borderRadius: tokens.borderRadiusMedium, ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
  },
  meta: { color: tokens.colorNeutralForeground3, whiteSpace: 'nowrap' },
  detail: { display: 'flex', flexDirection: 'column', gap: '6px', margin: '2px 0 8px 22px' },
  pre: {
    margin: 0, padding: '8px', maxHeight: '260px', overflow: 'auto', whiteSpace: 'pre-wrap', wordBreak: 'break-word',
    fontFamily: tokens.fontFamilyMonospace, fontSize: tokens.fontSizeBase100, backgroundColor: tokens.colorNeutralBackground3, borderRadius: tokens.borderRadiusMedium,
  },
})

/** Über dmOffice → Open-Source-Bibliotheken: Dank, Liste mit Lizenz und Link, volle Lizenztexte zum Aufklappen. */
export function OpenSourceDialog({ onClose }: { onClose: () => void }) {
  const s = useStyles()
  const [data, setData] = useState<List | null>(null)
  const [error, setError] = useState(false)
  const [query, setQuery] = useState('')
  const [open, setOpen] = useState<string | null>(null)
  useEffect(() => {
    fetch(import.meta.env.BASE_URL + 'third-party.json').then(r => r.ok ? r.json() : Promise.reject()).then(setData, () => setError(true))
  }, [])
  const sections = useMemo(() => {
    if (!data) return []
    const q = query.trim().toLowerCase()
    return data.sections.map(sec => ({
      ...sec,
      entries: q ? sec.entries.filter(e => [e.name, e.license, e.note?.[lang], ...(e.packages || [])].some(x => x?.toLowerCase().includes(q))) : sec.entries,
    })).filter(sec => sec.entries.length)
  }, [data, query])

  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose() }}>
      <DialogSurface style={{ maxWidth: 760 }}>
        <DialogBody>
          <DialogTitle>{t('Open-Source-Bibliotheken')}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
            <Text>{t('dmOffice baut auf Open-Source-Software auf. Vielen Dank allen, die diese Projekte entwickeln und pflegen.')}</Text>
            <Input contentBefore={<Search20Regular />} placeholder={t('Suchen')} value={query} onChange={(_, d) => setQuery(d.value)} />
            {error ? <Text>{t('Die Liste ließ sich nicht laden.')}</Text> : !data ? <Spinner size="small" /> : (
              <div className={s.list}>
                {sections.map(sec => (
                  <section key={sec.id}>
                    <Text weight="semibold" size={400} block>{t(TITLES[sec.id] ?? sec.id)} <span className={s.meta}>({sec.entries.length})</span></Text>
                    {NOTES[sec.id] && <Text size={200} block className={s.meta} style={{ whiteSpace: 'normal' }}>{t(NOTES[sec.id])}</Text>}
                    {sec.entries.map(e => {
                      const key = sec.id + '|' + e.name + '|' + e.license
                      const expanded = open === key
                      return (
                        <div key={key}>
                          <button type="button" className={s.row} aria-expanded={expanded} onClick={() => setOpen(expanded ? null : key)}>
                            {expanded ? <ChevronDown16Regular /> : <ChevronRight16Regular />}
                            <span>
                              <Text weight="semibold">{e.name}</Text>
                              {e.packages && <Text size={200} className={s.meta}> · {t('{n} Pakete', { n: e.packages.length })}</Text>}
                              {e.note?.[lang] && <Text size={200} block className={s.meta} style={{ whiteSpace: 'normal' }}>{e.note[lang]}</Text>}
                            </span>
                            <Text size={200} className={s.meta}>{[e.version, e.license].filter(x => x && x !== '–').join(' · ')}</Text>
                          </button>
                          {expanded && (
                            <div className={s.detail}>
                              {e.url && <Link href={e.url} target="_blank" rel="noopener noreferrer">{e.url.replace(/^https?:\/\//, '')}</Link>}
                              {e.copyright && <Text size={200}>{e.copyright}</Text>}
                              {e.author && <Text size={200}>{t('Urheber')}: {e.author}</Text>}
                              {e.packages && <Text size={200} className={s.meta} style={{ whiteSpace: 'normal' }}>{e.packages.join(', ')}</Text>}
                              {(e.texts || []).map(k => data.texts[k] && <pre key={k} className={s.pre}>{data.texts[k]}</pre>)}
                              {!e.texts?.length && e.license !== '–' && <Text size={200} className={s.meta}>{t('Lizenztext unter der genannten Adresse.')}</Text>}
                            </div>
                          )}
                        </div>
                      )
                    })}
                  </section>
                ))}
              </div>
            )}
          </DialogContent>
          <DialogActions>
            <Button appearance="primary" onClick={onClose}>{t('Schließen')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
