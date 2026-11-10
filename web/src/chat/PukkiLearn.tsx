import { Button, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Text, tokens, Tooltip } from '@fluentui/react-components'
import { Delete16Regular } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { Hint, Section } from '../settings/parts'

interface Entry { message: number; at: string; text: string; answer: string; verdict: string; expected: string | null }
interface Pref { kind: string; word: string; name: string; score: number }

const verdictLabel = (v: string) =>
  v === 'good' ? '👍 ' + t('richtig verstanden')
    : v === 'wrong' ? '👎 ' + t('falsch verstanden')
    : v === 'target' ? '👎 ' + t('falscher Kalender / falsche Liste')
    : v === 'time' ? '👎 ' + t('falsche Zeit')
    : v === 'ai' ? '👎 ' + t('KI-Antwort unpassend')
    : '👎 ' + t('nicht passend')

const muted = { color: tokens.colorNeutralForeground3 }

/** Einstellungen → Chat: was Pukki von mir gelernt hat - Vorlieben und Lernliste. Sieht nur der Benutzer selbst. */
export function PukkiLearnSection() {
  const { fail, notify } = useApp()
  const [data, setData] = useState<{ entries: Entry[]; prefs: Pref[] } | null>(null)
  const load = () => api.get<{ entries: Entry[]; prefs: Pref[] }>('/api/chat/pukki/learn').then(setData).catch(fail)
  useEffect(() => { load() }, []) // eslint-disable-line react-hooks/exhaustive-deps
  if (!data) return null
  const run = async (f: () => Promise<unknown>, done?: string) => { try { await f(); if (done) notify(done); load() } catch (e) { fail(e) } }
  return (
    <Section title={t('Was Pukki von mir gelernt hat')}>
      <Hint>{t('Mit 👍 und 👎 neben Pukkis Antworten zeigst du ihm, ob er dich richtig verstanden hat. Das bleibt bei dir – niemand sonst sieht es.')}</Hint>
      {data.prefs.length > 0 && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
          <Text weight="semibold">{t('Vorlieben')}</Text>
          {data.prefs.map(p => (
            <div key={p.kind + p.word} style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
              <Text>„{p.word}“ → {p.name || t('gewählter Kalender')} <span style={muted}>({p.kind === 'task' ? t('Aufgaben') : t('Termine')})</span></Text>
              <Tooltip content={t('Vergessen')} relationship="label">
                <Button size="small" appearance="subtle" icon={<Delete16Regular />} onClick={() => run(() => api.del(`/api/chat/pukki/prefs/${p.kind}/${encodeURIComponent(p.word)}`))} />
              </Tooltip>
            </div>
          ))}
        </div>
      )}
      {data.entries.length === 0 ? <Text style={muted}>{t('Noch keine Bewertungen.')}</Text> : (
        <Table size="small" style={{ maxWidth: 760 }}>
          <TableHeader>
            <TableRow>
              <TableHeaderCell>{t('Du hast geschrieben')}</TableHeaderCell>
              <TableHeaderCell>{t('Bewertung')}</TableHeaderCell>
              <TableHeaderCell>{t('Erwartet')}</TableHeaderCell>
              <TableHeaderCell style={{ width: 90 }} />
            </TableRow>
          </TableHeader>
          <TableBody>
            {data.entries.map(e => (
              <TableRow key={e.message}>
                <TableCell><Tooltip content={t('Pukki: {text}', { text: e.answer })} relationship="description"><span>{e.text || '–'}</span></Tooltip></TableCell>
                <TableCell>{verdictLabel(e.verdict)}</TableCell>
                <TableCell>{e.expected ?? ''}</TableCell>
                <TableCell>
                  <Tooltip content={t('Entfernen')} relationship="label">
                    <Button size="small" appearance="subtle" icon={<Delete16Regular />} onClick={() => run(() => api.del(`/api/chat/pukki/learn/${e.message}`))} />
                  </Tooltip>
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
    </Section>
  )
}

