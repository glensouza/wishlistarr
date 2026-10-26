import { useEffect, useRef, useState } from 'react'
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

const repoPath = '/glensouza/wishlistarr'
const releasePrefix = `${repoPath}/releases/tag/`
const upgradeFallbackUrl = `https://github.com${repoPath}/blob/main/docs/upgrading.md`
const semVer =
  /^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$/

function upgradeNotesUrl(available: Available): string | null {
  if (available.upgradeNotesUrl === upgradeFallbackUrl)
    return upgradeFallbackUrl
  if (!semVer.test(available.version)) return null
  return githubUrl(
    available.upgradeNotesUrl,
    (pathname) =>
      pathname === `${repoPath}/blob/v${available.version}/docs/upgrading.md`,
  )
}

type State =
  | { status: 'loading' }
  | { status: 'failed' }
  | { status: 'ready'; notice: Notice }

async function loadNotice(): Promise<State> {
  try {
    const body = await request<unknown>('GET', '/api/v1/updates')
    return isNotice(body)
      ? { status: 'ready', notice: body }
      : { status: 'failed' }
  } catch {
    return { status: 'failed' }
  }
}

export default function UpdateNotice() {
  const t = useT()
  const [state, setState] = useState<State>({ status: 'loading' })
  const [error, setError] = useState<string | null>(null)

  const mounted = useRef(true)

  useEffect(() => {
    mounted.current = true
    void loadNotice().then((next) => mounted.current && setState(next))
    return () => {
      mounted.current = false
    }
  }, [])

  async function retry() {
    setState({ status: 'loading' })
    const next = await loadNotice()
    if (mounted.current) setState(next)
  }

  async function toggle(checkEnabled: boolean) {
    setError(null)
    try {
      await request('PUT', '/api/v1/updates', { checkEnabled })
    } catch {
      setError(t('updates.saveFailed'))
    }
    setState(await loadNotice())
  }

  const notice = state.status === 'ready' ? state.notice : null
  const available = notice?.checkEnabled ? notice.available : null
  const releaseHref = available
    ? githubUrl(
        available.releaseNotesUrl,
        (pathname) =>
          pathname.startsWith(releasePrefix) &&
          pathname.length > releasePrefix.length,
      )
    : null
  const upgradeHref = available ? upgradeNotesUrl(available) : null
  const showLinks =
    available !== null && releaseHref !== null && upgradeHref !== null

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
          disabled={notice === null}
          onChange={(event) => void toggle(event.target.checked)}
        />
        {t('updates.check')}
      </label>
      {state.status === 'failed' && (
        <p role="alert" className="error">
          {t('updates.loadFailed')}{' '}
          <button type="button" onClick={() => void retry()}>
            {t('updates.retry')}
          </button>
        </p>
      )}
      <p className="muted">{t('updates.hint')}</p>
      {error && (
        <p role="alert" className="error">
          {error}
        </p>
      )}
    </section>
  )
}
