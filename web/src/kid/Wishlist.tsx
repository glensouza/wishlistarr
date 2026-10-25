import { useEffect, useId, useRef, useState } from 'react'
import { ApiError } from '../api'
import { useT, type MessageKey } from '../i18n'
import {
  getWishlist,
  type KidPlexLinks,
  type KidWish,
  type KidWishlist,
} from './kidApi'
import { useKidSession } from './KidSession'

export const wishlistPollMs = 15_000
export const wishlistPollMaxMs = 60_000

function labelFor(state: string): MessageKey {
  switch (state) {
    case 'waiting':
      return 'wishlist.waiting'
    case 'onTheWay':
      return 'wishlist.onTheWay'
    case 'ready':
      return 'wishlist.ready'
    case 'denied':
      return 'wishlist.denied'
    default:
      return 'wishlist.unknown'
  }
}

const titleOf = (item: KidWish, untitled: string) =>
  item.title?.trim() ? item.title : untitled

function formatWhen(value: string | null) {
  if (!value) return null
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return null
  return new Intl.DateTimeFormat(undefined, { dateStyle: 'medium' }).format(
    date,
  )
}

function acceptedUrl(url: string | undefined, prefix: string) {
  if (
    !url ||
    !url.startsWith(prefix) ||
    /X-Plex-Token/i.test(url) ||
    /:32400\b/.test(url)
  ) {
    return null
  }
  return url
}

const webLinkFor = (plex: KidPlexLinks | null) =>
  acceptedUrl(plex?.webUrl, 'https://app.plex.tv/')

export default function Wishlist({
  pollMs = wishlistPollMs,
  pollMaxMs = wishlistPollMaxMs,
}: {
  pollMs?: number
  pollMaxMs?: number
}) {
  const t = useT()
  const titleId = useId()
  const [wishlist, setWishlist] = useState<KidWishlist | null>(null)
  const [status, setStatus] = useState<'loading' | 'ready' | 'failed'>(
    'loading',
  )
  const [attempt, setAttempt] = useState(0)
  const [refreshFailed, setRefreshFailed] = useState(false)
  const [announcement, setAnnouncement] = useState('')
  const { clear } = useKidSession()
  const known = useRef<Map<number, string> | null>(null)

  useEffect(() => {
    let delay = pollMs
    let timer: ReturnType<typeof setTimeout> | undefined
    let stopped = false
    let inFlight = false
    const controller = new AbortController()

    const schedule = () => {
      clearTimeout(timer)
      if (stopped || document.hidden) return
      timer = setTimeout(() => void run(), delay)
    }

    const announce = (next: KidWishlist) => {
      const before = known.current
      const changes: string[] = []
      for (const item of next.items) {
        const previous = before?.get(item.id)
        if (previous !== undefined && previous !== item.state) {
          changes.push(
            t('wishlist.changed', {
              title: titleOf(item, t('wishlist.untitled')),
              state: t(labelFor(item.state)),
            }),
          )
        }
      }
      known.current = new Map(next.items.map((item) => [item.id, item.state]))
      if (changes.length > 0) setAnnouncement(changes.join('. '))
    }

    const run = async () => {
      if (stopped || document.hidden || inFlight) return
      inFlight = true
      try {
        const next = await getWishlist(controller.signal)
        if (stopped) return
        announce(next)
        setWishlist(next)
        setStatus('ready')
        setRefreshFailed(false)
        delay = pollMs
      } catch (failure) {
        if (stopped || controller.signal.aborted) return
        if (
          failure instanceof ApiError &&
          (failure.status === 401 || failure.status === 403)
        ) {
          stopped = true
          clearTimeout(timer)
          clear()
          return
        }
        setStatus((current) => (current === 'loading' ? 'failed' : current))
        setRefreshFailed(true)
        delay = Math.min(delay * 2, pollMaxMs)
      } finally {
        inFlight = false
      }
      schedule()
    }

    const onVisibility = () => {
      clearTimeout(timer)
      if (document.hidden) return
      delay = pollMs
      void run()
    }

    document.addEventListener('visibilitychange', onVisibility)
    void run()
    return () => {
      stopped = true
      clearTimeout(timer)
      controller.abort()
      document.removeEventListener('visibilitychange', onVisibility)
    }
  }, [attempt, pollMs, pollMaxMs, t, clear])

  if (status === 'loading' && !wishlist) {
    return <p role="status">{t('wishlist.loading')}</p>
  }
  if (status === 'failed' && !wishlist) {
    return (
      <div role="alert">
        <p className="error">{t('wishlist.failed')}</p>
        <button type="button" onClick={() => setAttempt((count) => count + 1)}>
          {t('wishlist.retry')}
        </button>
      </div>
    )
  }
  if (!wishlist) return null

  const when = formatWhen(wishlist.resetsAt)
  const remaining =
    wishlist.remaining === null
      ? t('wishlist.unlimited')
      : wishlist.remaining === 1
        ? t('wishlist.remainingOne')
        : wishlist.remaining > 1
          ? t('wishlist.remaining', { count: wishlist.remaining })
          : when
            ? t('wishlist.noneLeftWhen', { when })
            : t('wishlist.noneLeft')

  return (
    <section className="wishlist" aria-labelledby={titleId}>
      <h2 id={titleId}>{t('wishlist.title')}</h2>
      <p className="wishlist-remaining" role="status">
        {remaining}
      </p>
      <p className="visually-hidden" role="status" aria-live="polite">
        {announcement}
      </p>
      {refreshFailed && (
        <p className="wishlist-notice">
          {t('wishlist.refreshFailed')}
          <button
            type="button"
            onClick={() => setAttempt((count) => count + 1)}
          >
            {t('wishlist.retry')}
          </button>
        </p>
      )}
      {wishlist.items.length === 0 ? (
        <p className="wishlist-empty">{t('wishlist.empty')}</p>
      ) : (
        <ul className="wishlist-items">
          {wishlist.items.map((item) => (
            <WishCard key={item.id} item={item} />
          ))}
        </ul>
      )}
    </section>
  )
}

function WishCard({ item }: { item: KidWish }) {
  const t = useT()
  const titleId = useId()
  const title = titleOf(item, t('wishlist.untitled'))
  const label = labelFor(item.state)
  const web = item.state === 'ready' ? webLinkFor(item.plex) : null
  const note = item.state === 'denied' && item.note ? item.note : null

  return (
    <li className="wish-card" aria-labelledby={titleId}>
      <h3 id={titleId}>{title}</h3>
      <p className="muted">
        {item.kind === 'movie' ? t('search.movie') : t('search.show')}
        {' · '}
        <span
          className={`badge badge-${label === 'wishlist.unknown' ? 'unknown' : item.state}`}
        >
          {t(label)}
        </span>
      </p>
      {note && <p className="wish-note">{note}</p>}
      {web && (
        <div className="wish-links">
          <a className="wish-link" href={web}>
            {t('wishlist.watch', { title })}
          </a>
        </div>
      )}
    </li>
  )
}
