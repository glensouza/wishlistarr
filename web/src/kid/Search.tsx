import { useEffect, useId, useRef, useState, type KeyboardEvent } from 'react'
import { Link } from 'react-router-dom'
import { ApiError } from '../api'
import { useT, type MessageKey } from '../i18n'
import {
  requestTitle,
  searchTitles,
  type SearchItem,
  type SearchPage,
} from './kidApi'

export const searchDebounceMs = 300
export const searchMinLength = 2
export const searchMaxPage = 10

type Status = 'idle' | 'loading' | 'ready' | 'failed'
type Ask = { state: 'sending' | 'done' | 'failed'; message?: MessageKey }

const keyOf = (item: SearchItem) => `${item.kind}:${item.tmdbId}`

const normalize = (value: string) => value.trim().replace(/\s+/g, ' ')

export default function Search() {
  const t = useT()
  const inputId = useId()
  const [query, setQuery] = useState('')
  const [status, setStatus] = useState<Status>('idle')
  const [page, setPage] = useState<SearchPage | null>(null)
  const [asks, setAsks] = useState<Record<string, Ask>>({})
  const [retry, setRetry] = useState(0)
  const more = useRef<AbortController | null>(null)
  const [loadingMore, setLoadingMore] = useState(false)
  const [moreFailed, setMoreFailed] = useState(false)

  const term = normalize(query)
  const searchable = term.length >= searchMinLength

  useEffect(() => {
    if (!searchable) return
    const controller = new AbortController()
    const timer = setTimeout(() => {
      setStatus('loading')
      setMoreFailed(false)
      void (async () => {
        try {
          let loaded = await searchTitles(term, 1, controller.signal)
          while (
            loaded.results.length === 0 &&
            loaded.hasMore &&
            loaded.page < searchMaxPage
          ) {
            loaded = await searchTitles(
              loaded.query,
              loaded.page + 1,
              controller.signal,
            )
          }
          if (controller.signal.aborted) return
          setPage(loaded)
          setStatus('ready')
        } catch {
          if (controller.signal.aborted) return
          setStatus('failed')
        }
      })()
    }, searchDebounceMs)
    return () => {
      clearTimeout(timer)
      controller.abort()
      more.current?.abort()
      more.current = null
      setLoadingMore(false)
    }
  }, [term, searchable, retry])

  function showMore() {
    if (!page) return
    const controller = new AbortController()
    more.current = controller
    setLoadingMore(true)
    setMoreFailed(false)
    searchTitles(page.query, page.page + 1, controller.signal).then(
      (next) => {
        if (controller.signal.aborted) return
        const seen = new Set(page.results.map(keyOf))
        setPage({
          ...next,
          results: [
            ...page.results,
            ...next.results.filter((item) => !seen.has(keyOf(item))),
          ],
        })
        setLoadingMore(false)
      },
      () => {
        if (controller.signal.aborted) return
        setMoreFailed(true)
        setLoadingMore(false)
      },
    )
  }

  async function ask(item: SearchItem) {
    const key = keyOf(item)
    setAsks((current) => ({ ...current, [key]: { state: 'sending' } }))
    try {
      const made = await requestTitle(item.kind, item.tmdbId)
      const message: MessageKey = made.alreadyAsked
        ? 'search.already'
        : made.state === 'labeled'
          ? 'search.added'
          : 'search.requested'
      setAsks((current) => ({ ...current, [key]: { state: 'done', message } }))
    } catch (failure) {
      const code = failure instanceof ApiError ? failure.status : 0
      const message: MessageKey =
        code === 422
          ? 'search.notAllowed'
          : code === 429
            ? 'search.allowance'
            : 'search.requestFailed'
      setAsks((current) => ({
        ...current,
        [key]: { state: 'failed', message },
      }))
    }
  }

  function onKeyDown(event: KeyboardEvent<HTMLInputElement>) {
    if (event.key === 'Escape' && query !== '') {
      event.preventDefault()
      setQuery('')
    }
  }

  const showing: Status = searchable ? status : 'idle'
  const results = showing === 'ready' && page ? page.results : []

  return (
    <section className="search" aria-labelledby={`${inputId}-title`}>
      <h2 id={`${inputId}-title`}>{t('search.title')}</h2>
      <label htmlFor={inputId}>{t('search.label')}</label>
      <input
        id={inputId}
        className="search-input"
        type="search"
        autoComplete="off"
        enterKeyHint="search"
        value={query}
        maxLength={100}
        onChange={(event) => setQuery(event.target.value)}
        onKeyDown={onKeyDown}
      />
      {!searchable && term !== '' && (
        <p className="muted">{t('search.hint', { min: searchMinLength })}</p>
      )}
      <p
        role="status"
        className={showing === 'ready' ? 'visually-hidden' : 'muted'}
      >
        {showing === 'loading' && t('search.loading')}
        {showing === 'ready' &&
          (results.length === 1
            ? t('search.foundOne')
            : t('search.found', { count: results.length }))}
      </p>
      {showing === 'failed' && (
        <div role="alert">
          <p className="error">{t('search.failed')}</p>
          <button type="button" onClick={() => setRetry((count) => count + 1)}>
            {t('search.retry')}
          </button>
        </div>
      )}
      {showing === 'ready' && page && results.length === 0 && !page.hasMore && (
        <p className="search-empty">
          {t('search.empty', { query: page.query })}
        </p>
      )}
      {results.length > 0 && (
        <ul className="search-results">
          {results.map((item) => (
            <ResultCard
              key={keyOf(item)}
              item={item}
              ask={asks[keyOf(item)]}
              onAsk={() => void ask(item)}
            />
          ))}
        </ul>
      )}
      {showing === 'ready' && moreFailed && (
        <div role="alert">
          <p className="error">{t('search.moreFailed')}</p>
          <button type="button" onClick={showMore}>
            {t('search.retry')}
          </button>
        </div>
      )}
      {showing === 'ready' && page?.hasMore && !moreFailed && (
        <button type="button" onClick={showMore} disabled={loadingMore}>
          {loadingMore ? t('search.loading') : t('search.more')}
        </button>
      )}
    </section>
  )
}

function ResultCard({
  item,
  ask,
  onAsk,
}: {
  item: SearchItem
  ask: Ask | undefined
  onAsk: () => void
}) {
  const t = useT()
  const titleId = useId()
  const busy = ask?.state === 'sending'
  const done = ask?.state === 'done'
  return (
    <li className="result-card" aria-labelledby={titleId}>
      {item.posterUrl ? (
        <img
          className="result-poster"
          src={item.posterUrl}
          alt=""
          loading="lazy"
        />
      ) : (
        <div className="result-poster" aria-hidden="true" />
      )}
      <div className="result-body">
        <h3 id={titleId}>
          <Link
            className="title-link"
            to={`/title/${item.kind}/${item.tmdbId}`}
          >
            {item.title}
            {item.year ? ` (${item.year})` : ''}
          </Link>
        </h3>
        <p className="muted">
          {item.kind === 'movie' ? t('search.movie') : t('search.show')}
          {item.needsParent && (
            <>
              {' · '}
              <span className="badge badge-needsParent">
                {t('search.askParent')}
              </span>
            </>
          )}
          {item.availability && (
            <>
              {' · '}
              <span className={`badge badge-${item.availability}`}>
                {item.availability === 'inLibrary'
                  ? t('search.inLibrary')
                  : t('search.notInLibrary')}
              </span>
            </>
          )}
        </p>
        {item.overview && <p className="result-overview">{item.overview}</p>}
        <button
          type="button"
          className="result-ask"
          disabled={busy || done}
          aria-label={t('search.ask', { title: item.title })}
          onClick={onAsk}
        >
          {busy ? t('search.asking') : t('search.askShort')}
        </button>
        <p
          role="status"
          className={ask?.state === 'failed' ? 'error' : 'muted'}
        >
          {ask?.message ? t(ask.message) : ''}
        </p>
      </div>
    </li>
  )
}
