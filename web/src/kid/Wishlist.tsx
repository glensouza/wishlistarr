import { useEffect, useId, useState } from 'react'
import { useT, type MessageKey } from '../i18n'
import {
  getWishlist,
  type KidPlexLinks,
  type KidWish,
  type KidWishlist,
} from './kidApi'

export const wishlistPollMs = 15_000
export const wishlistPollMaxMs = 60_000

const stateLabel: Record<KidWish['state'], MessageKey> = {
  waiting: 'wishlist.waiting',
  onTheWay: 'wishlist.onTheWay',
  ready: 'wishlist.ready',
  denied: 'wishlist.denied',
}

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

function linksFor(plex: KidPlexLinks | null) {
  return {
    web: acceptedUrl(plex?.webUrl, 'https://app.plex.tv/'),
    app: acceptedUrl(plex?.appUrl, 'plex://'),
  }
}

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

  useEffect(() => {
    let delay = pollMs
    let timer: ReturnType<typeof setTimeout> | undefined
    let stopped = false
    const controller = new AbortController()

    const schedule = () => {
      if (stopped || document.hidden) return
      timer = setTimeout(() => void run(), delay)
    }

    const run = async () => {
      if (stopped || document.hidden) return
      try {
        const next = await getWishlist(controller.signal)
        if (stopped) return
        setWishlist(next)
        setStatus('ready')
        delay = pollMs
      } catch {
        if (stopped || controller.signal.aborted) return
        setStatus((current) => (current === 'loading' ? 'failed' : current))
        delay = Math.min(delay * 2, pollMaxMs)
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
  }, [attempt, pollMs, pollMaxMs])

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
  const title = item.title?.trim() ? item.title : t('wishlist.untitled')
  const label = stateLabel[item.state]
  const links =
    item.state === 'ready' ? linksFor(item.plex) : { web: null, app: null }
  const note = item.state === 'denied' && item.note ? item.note : null

  return (
    <li className="wish-card" aria-labelledby={titleId}>
      <h3 id={titleId}>{title}</h3>
      <p className="muted">
        {item.kind === 'movie' ? t('search.movie') : t('search.show')}
        {label && (
          <>
            {' · '}
            <span className={`badge badge-${item.state}`} aria-live="polite">
              {t(label)}
            </span>
          </>
        )}
      </p>
      {note && <p className="wish-note">{note}</p>}
      {(links.web || links.app) && (
        <div className="wish-links">
          {links.web && (
            <a className="wish-link" href={links.web}>
              {t('wishlist.watch', { title })}
            </a>
          )}
          {links.app && (
            <a className="wish-link wish-link-app" href={links.app}>
              {t('wishlist.openApp', { title })}
            </a>
          )}
        </div>
      )}
    </li>
  )
}
