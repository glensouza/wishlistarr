import { useEffect, useState } from 'react'
import { request } from './api'
import { useT } from './i18n'

type Available = {
  version: string
  releaseNotesUrl: string
  upgradeNotesUrl: string
}

type Notice = {
  checkEnabled: boolean
  available: Available | null
}

const upgradeNotesUrl =
  'https://github.com/glensouza/wishlistarr/blob/main/docs/upgrading.md'

function isNotice(value: unknown): value is Notice {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false
  const notice = value as { checkEnabled?: unknown; available?: unknown }
  if (typeof notice.checkEnabled !== 'boolean') return false
  if (notice.available === null) return true
  if (!notice.available || typeof notice.available !== 'object') return false
  const available = notice.available as Available
  return (
    typeof available.version === 'string' &&
    typeof available.releaseNotesUrl === 'string' &&
    typeof available.upgradeNotesUrl === 'string'
  )
}

function githubUrl(
  url: string,
  pathOk: (pathname: string) => boolean,
): string | null {
  let parsed: URL
  try {
    parsed = new URL(url)
  } catch {
    return null
  }
  if (
    parsed.protocol !== 'https:' ||
    parsed.hostname !== 'github.com' ||
    parsed.username !== '' ||
    parsed.password !== '' ||
    parsed.port !== '' ||
    parsed.search !== '' ||
    parsed.hash !== '' ||
    !pathOk(parsed.pathname)
  ) {
    return null
  }
  return url
}

function releaseNotesHref(url: string): string | null {
  return githubUrl(
    url,
    (pathname) =>
      pathname.startsWith('/glensouza/wishlistarr/releases/tag/') &&
      pathname.length > '/glensouza/wishlistarr/releases/tag/'.length,
  )
}

function upgradeNotesHref(url: string): string | null {
  return githubUrl(
    url,
    (pathname) =>
      pathname === '/glensouza/wishlistarr/blob/main/docs/upgrading.md',
  )
}

export default function UpdateNotice() {
  const t = useT()
  const [notice, setNotice] = useState<Notice | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let current = true
    request<unknown>('GET', '/api/v1/updates').then(
      (body) => current && setNotice(isNotice(body) ? body : null),
      () => current && setNotice(null),
    )
    return () => {
      current = false
    }
  }, [])

  async function toggle(checkEnabled: boolean) {
    setError(null)
    try {
      await request('PUT', '/api/v1/updates', { checkEnabled })
    } catch {
      setError(t('updates.saveFailed'))
    }
    try {
      const body = await request<unknown>('GET', '/api/v1/updates')
      setNotice(isNotice(body) ? body : null)
    } catch {
      setNotice(null)
    }
  }

  const available = notice?.checkEnabled ? notice.available : null
  const releaseHref = available
    ? releaseNotesHref(available.releaseNotesUrl)
    : null
  const upgradeHref = available
    ? upgradeNotesHref(available.upgradeNotesUrl)
    : null
  const showLinks =
    available !== null &&
    releaseHref !== null &&
    upgradeHref !== null &&
    upgradeHref === upgradeNotesUrl

  return (
    <section className="update-notice">
      <h2>{t('updates.title')}</h2>
      {showLinks && (
        <p role="status">
          {t('updates.available', { version: available.version })}{' '}
          <a href={releaseHref}>{t('updates.releaseNotes')}</a>{' '}
          <a href={upgradeHref}>{t('updates.upgradeNotes')}</a>
        </p>
      )}
      <label className="check-row">
        <input
          type="checkbox"
          checked={notice?.checkEnabled === true}
          onChange={(event) => void toggle(event.target.checked)}
        />
        {t('updates.check')}
      </label>
      <p className="muted">{t('updates.hint')}</p>
      {error && (
        <p role="alert" className="error">
          {error}
        </p>
      )}
    </section>
  )
}
