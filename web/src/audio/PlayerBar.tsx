import { Button, Slider, Text, Tooltip, makeStyles, mergeClasses, tokens } from '@fluentui/react-components'
import {
  ArrowRepeat120Regular, ArrowRepeatAll20Regular, Dismiss20Regular, ArrowRepeatAllOff20Regular, ArrowShuffle20Regular, ArrowShuffleOff20Regular, MusicNote224Regular,
  Next20Filled, Pause24Filled, Play24Filled, Previous20Filled, Speaker220Regular, SpeakerMute20Regular, TextBulletListLtr20Regular,
} from '@fluentui/react-icons'
import { useState } from 'react'
import { t } from '../i18n'
import { coverUrl, fmtDuration } from './library'
import { player, usePlayer } from './player'

const useStyles = makeStyles({
  bar: {
    display: 'flex', alignItems: 'center', gap: '12px', padding: '6px 12px', flexShrink: 0, minHeight: '64px', boxSizing: 'border-box',
    backgroundColor: tokens.colorNeutralBackground1, borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  // Handy: über der Leiste mit den Bereichen, kompakt; Fortschritt als dünner Strich oben
  mobile: { gap: '8px', padding: '4px 8px', minHeight: '56px', position: 'relative', order: 1 },
  now: { display: 'flex', alignItems: 'center', gap: '10px', minWidth: 0, flex: '1 1 0', cursor: 'pointer' },
  cover: { width: '48px', height: '48px', borderRadius: tokens.borderRadiusMedium, objectFit: 'cover', flexShrink: 0, backgroundColor: tokens.colorNeutralBackground3,
    display: 'flex', alignItems: 'center', justifyContent: 'center', color: tokens.colorNeutralForeground3 },
  coverSmall: { width: '40px', height: '40px' },
  text: { display: 'flex', flexDirection: 'column', minWidth: 0 },
  ellipsis: { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  error: { color: tokens.colorPaletteRedForeground1 },
  center: { display: 'flex', flexDirection: 'column', alignItems: 'center', flex: '2 1 0', minWidth: 0, maxWidth: '640px' },
  controls: { display: 'flex', alignItems: 'center', gap: '4px' },
  progress: { display: 'flex', alignItems: 'center', gap: '8px', width: '100%' },
  time: { fontSize: '12px', color: tokens.colorNeutralForeground3, minWidth: '40px', textAlign: 'center', fontVariantNumeric: 'tabular-nums' },
  slider: { flex: 1, minWidth: 0 },
  right: { display: 'flex', alignItems: 'center', justifyContent: 'flex-end', gap: '4px', flex: '1 1 0' },
  volume: { width: '110px', minWidth: 0 },
  on: { color: tokens.colorBrandForeground1 },
  line: { position: 'absolute', left: 0, right: 0, top: 0, height: '2px', backgroundColor: tokens.colorNeutralStroke2 },
  lineFill: { height: '100%', backgroundColor: tokens.colorBrandBackground },
})

function Cover({ id, small }: { id: string | null; small?: boolean }) {
  const s = useStyles()
  const [broken, setBroken] = useState(false)
  if (id && !broken) return <img className={mergeClasses(s.cover, small && s.coverSmall)} src={coverUrl(id)} alt="" onError={() => setBroken(true)} />
  return <div className={mergeClasses(s.cover, small && s.coverSmall)}><MusicNote224Regular /></div>
}

/** Leiste unten mit dem laufenden Titel - bleibt in allen Bereichen stehen, solange etwas in der Warteschlange ist. */
export function PlayerBar({ mobile }: { mobile: boolean }) {
  const s = useStyles()
  const p = usePlayer()
  const cur = p.queue[p.index]
  if (!cur) return null
  const duration = p.duration || cur.duration || 0
  const openQueue = () => { location.hash = '#/audio/queue' }
  const playLabel = p.playing ? t('Pause') : t('Abspielen')
  const sub = p.error ?? [cur.artist, cur.album].filter(Boolean).join(' · ')
  if (mobile) return (
    <div className={mergeClasses(s.bar, s.mobile)}>
      <div className={s.line}><div className={s.lineFill} style={{ width: duration ? `${Math.min(100, (p.position / duration) * 100)}%` : 0 }} /></div>
      <div className={s.now} onClick={openQueue}>
        <Cover id={cur.cover} small />
        <div className={s.text}>
          <Text weight="semibold" className={s.ellipsis}>{cur.title}</Text>
          <Text size={200} className={mergeClasses(s.ellipsis, p.error ? s.error : s.muted)}>{sub}</Text>
        </div>
      </div>
      <Button appearance="subtle" size="large" icon={p.playing ? <Pause24Filled /> : <Play24Filled />} onClick={() => player.toggle()} aria-label={playLabel} />
      <Button appearance="subtle" icon={<Next20Filled />} onClick={() => player.next()} aria-label={t('Nächster Titel')} disabled={p.index >= p.queue.length - 1 && p.repeat !== 'all'} />
      <Button appearance="subtle" icon={<Dismiss20Regular />} onClick={() => player.clear()} aria-label={t('Wiedergabe beenden')} />
    </div>
  )
  const repeatLabel = p.repeat === 'off' ? t('Wiederholen: aus') : p.repeat === 'all' ? t('Wiederholen: alle') : t('Wiederholen: diesen Titel')
  return (
    <div className={s.bar}>
      <div className={s.now} onClick={openQueue} title={t('Warteschlange')}>
        <Cover id={cur.cover} />
        <div className={s.text}>
          <Text weight="semibold" className={s.ellipsis}>{cur.title}</Text>
          <Text size={200} className={mergeClasses(s.ellipsis, p.error ? s.error : s.muted)}>{sub}</Text>
        </div>
      </div>
      <div className={s.center}>
        <div className={s.controls}>
          <Tooltip content={p.shuffle ? t('Zufall: an') : t('Zufall: aus')} relationship="label">
            <Button appearance="subtle" className={p.shuffle ? s.on : undefined} icon={p.shuffle ? <ArrowShuffle20Regular /> : <ArrowShuffleOff20Regular />}
              onClick={() => player.toggleShuffle()} aria-pressed={p.shuffle} />
          </Tooltip>
          <Tooltip content={t('Vorheriger Titel')} relationship="label">
            <Button appearance="subtle" icon={<Previous20Filled />} onClick={() => player.prev()} />
          </Tooltip>
          <Tooltip content={playLabel} relationship="label">
            <Button appearance="primary" shape="circular" icon={p.playing ? <Pause24Filled /> : <Play24Filled />} onClick={() => player.toggle()} />
          </Tooltip>
          <Tooltip content={t('Nächster Titel')} relationship="label">
            <Button appearance="subtle" icon={<Next20Filled />} onClick={() => player.next()} disabled={p.index >= p.queue.length - 1 && p.repeat !== 'all'} />
          </Tooltip>
          <Tooltip content={repeatLabel} relationship="label">
            <Button appearance="subtle" className={p.repeat !== 'off' ? s.on : undefined}
              icon={p.repeat === 'one' ? <ArrowRepeat120Regular /> : p.repeat === 'all' ? <ArrowRepeatAll20Regular /> : <ArrowRepeatAllOff20Regular />}
              onClick={() => player.cycleRepeat()} />
          </Tooltip>
        </div>
        <div className={s.progress}>
          <span className={s.time}>{fmtDuration(p.position)}</span>
          <Slider className={s.slider} size="small" min={0} max={Math.max(1, Math.floor(duration))} value={Math.min(Math.floor(p.position), Math.floor(duration) || 1)}
            onChange={(_, d) => player.seek(d.value)} aria-label={t('Position')} />
          <span className={s.time}>{fmtDuration(duration)}</span>
        </div>
      </div>
      <div className={s.right}>
        <Tooltip content={t('Warteschlange')} relationship="label">
          <Button appearance="subtle" icon={<TextBulletListLtr20Regular />} onClick={openQueue} />
        </Tooltip>
        <Tooltip content={p.volume > 0 ? t('Stumm') : t('Ton an')} relationship="label">
          <Button appearance="subtle" icon={p.volume > 0 ? <Speaker220Regular /> : <SpeakerMute20Regular />}
            onClick={() => player.setVolume(p.volume > 0 ? 0 : 1)} />
        </Tooltip>
        <Slider className={s.volume} size="small" min={0} max={100} value={Math.round(p.volume * 100)} onChange={(_, d) => player.setVolume(d.value / 100)} aria-label={t('Lautstärke')} />
        {/* Beenden: Wiedergabe stoppen und Warteschlange leeren - dann verschwindet die Leiste */}
        <Tooltip content={t('Wiedergabe beenden (leert die Warteschlange)')} relationship="label">
          <Button appearance="subtle" icon={<Dismiss20Regular />} onClick={() => player.clear()} />
        </Tooltip>
      </div>
    </div>
  )
}
