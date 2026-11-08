import { Button } from '@fluentui/react-components'
import { Delete20Regular, ImageAdd20Regular } from '@fluentui/react-icons'
import { useEffect, useRef, useState } from 'react'
import { useApp } from '../App'
import { e2eFetch } from '../e2e'
import { t } from '../i18n'
import { usePrefs } from '../prefs'
import { Hint, Section } from '../settings/parts'
import { chatStatusChanged } from './store'
import { UserAvatar } from './UserAvatar'

/** Bild quadratisch aus der Mitte zuschneiden und auf 512 Pixel bringen (JPEG) - der Server macht daraus 256 Pixel WebP. */
async function square(file: File): Promise<Blob> {
  const img = await createImageBitmap(file)
  const side = Math.min(img.width, img.height)
  const canvas = document.createElement('canvas')
  canvas.width = canvas.height = Math.min(512, side)
  canvas.getContext('2d')!.drawImage(img, (img.width - side) / 2, (img.height - side) / 2, side, side, 0, 0, canvas.width, canvas.height)
  return new Promise((ok, fail) => canvas.toBlob(b => (b ? ok(b) : fail(new Error(t('Das Bild ließ sich nicht lesen.')))), 'image/jpeg', 0.9))
}

/** Einstellungen -> Chat: eigenes Profilbild (Version 2). Ohne Bild: Anfangsbuchstabe in der Akzentfarbe. */
export function ProfilePhoto() {
  const { me, fail } = useApp()
  const { prefs } = usePrefs()
  const [avatar, setAvatar] = useState<string | null | undefined>(undefined)
  const [busy, setBusy] = useState(false)
  const file = useRef<HTMLInputElement>(null)
  const url = '/api/chat/people/me/avatar'
  useEffect(() => { e2eFetch('/api/chat/status').then(r => r.json()).then(s => setAvatar(s.avatar ?? null)).catch(() => setAvatar(null)) }, [])
  const changed = (version: string | null) => { setAvatar(version); chatStatusChanged() }

  const upload = async (f: File) => {
    setBusy(true)
    try {
      const r = await e2eFetch(url, { method: 'PUT', headers: { 'Content-Type': 'image/jpeg' }, body: await square(f) })
      const data = await r.json().catch(() => null)
      if (!r.ok) throw new Error(data?.error || r.statusText)
      changed(data.avatar)
    } catch (e) { fail(e) } finally { setBusy(false) }
  }
  const remove = async () => {
    try { await e2eFetch(url, { method: 'DELETE' }); changed(null) } catch (e) { fail(e) }
  }

  return (
    <Section title={t('Profilbild')}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 16, flexWrap: 'wrap' }}>
        <UserAvatar id={me.id} name={me.displayName} login={me.login} avatar={avatar} accent={prefs.accent} size={72} />
        <Button icon={<ImageAdd20Regular />} disabled={busy} onClick={() => file.current?.click()}>{avatar ? t('Anderes Bild wählen') : t('Bild wählen')}</Button>
        {avatar && <Button appearance="subtle" icon={<Delete20Regular />} onClick={remove}>{t('Bild entfernen')}</Button>}
        <input ref={file} type="file" accept="image/*" hidden onChange={e => { const f = e.target.files?.[0]; if (f) upload(f); e.target.value = '' }} />
      </div>
      <Hint>{t('Andere sehen dein Bild im Chat. Ohne eigenes Bild erscheint der erste Buchstabe deines Benutzernamens in deiner Akzentfarbe.')}</Hint>
    </Section>
  )
}
