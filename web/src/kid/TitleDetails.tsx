import { useEffect, useId, useMemo, useRef, useState } from 'react'
import { Link, Navigate, useParams } from 'react-router-dom'
import { ApiError } from '../api'
import { useT, type MessageKey } from '../i18n'
import Artwork from './Artwork'
import { useKidSession } from './KidSession'
import Trailer from './Trailer'
import PlexTrailer from './PlexTrailer'
import { useKidSpatialNav } from './useKidSpatialNav'
import {
  getTitle,
  requestTitle,
  type TitleDetails as TitlePayload,
  type TitleSeason,
} from './kidApi'

type Phase = 'loading' | 'ready' | 'missing' | 'failed'

const requestLabels: Record<string, MessageKey> = {
  pending: 'details.requested',
  approved: 'details.onTheWay',
  fulfilling: 'details.onTheWay',
  available: 'details.ready',
  labeled: 'details.added',
  failed: 'details.failedRequest',
}

const askable = (state: string | null) => state === null || state === 'denied'

function parseRoute(kind: string | undefined, rawId: string | undefined) {
  if (kind !== 'movie' && kind !== 'show') return null
  if (!rawId || !/^[1-9][0-9]*$/.test(rawId)) return null
  const tmdbId = Number(rawId)
  if (!Number.isSafeInteger(tmdbId)) return null
  return { kind, tmdbId } as const
}

function seasonName(
  season: TitleSeason,
  t: (key: MessageKey, params?: Record<string, string | number>) => string,
) {
  if (season.name) return season.name
  if (season.number === 0) return t('details.specials')
  return t('details.seasonName', { number: season.number })
}

function TitleScreen() {
  const t = useT()
  const params = useParams()
  const route = useMemo(
    () => parseRoute(params.kind, params.tmdbId),
    [params.kind, params.tmdbId],
  )
  const { session } = useKidSession()
  const seasonGroup = useId()
  const [phase, setPhase] = useState<Phase>('loading')
  const [title, setTitle] = useState<TitlePayload | null>(null)
  const [request, setRequest] = useState<TitlePayload['request']>(null)
  const [picked, setPicked] = useState<number | null>(null)
  const [asking, setAsking] = useState(false)
  const [askError, setAskError] = useState<MessageKey | null>(null)
  const [retry, setRetry] = useState(0)

  useEffect(() => {
    if (session.status !== 'in' || !route) return
    const controller = new AbortController()
    let current = true
    getTitle(route.kind, route.tmdbId, controller.signal).then(
      (loaded) => {
        if (!current) return
        setTitle(loaded)
        setRequest(loaded.request)
        setPicked(null)
        setAskError(null)
        setPhase('ready')
      },
      (error: unknown) => {
        if (!current || controller.signal.aborted) return
        const status = error instanceof ApiError ? error.status : 0
        setPhase(status === 404 ? 'missing' : 'failed')
      },
    )
    return () => {
      current = false
      controller.abort()
    }
  }, [session.status, route, retry])

  if (session.status === 'loading') {
    return <p role="status">{t('app.loading')}</p>
  }
  if (session.status === 'out') {
    return <Navigate to="/" replace />
  }
  if (!route || phase === 'missing') {
    return (
      <>
        <h1>{t('details.missing')}</h1>
        <Link to="/home">{t('details.back')}</Link>
      </>
    )
  }
  if (phase === 'failed') {
    return (
      <div role="alert">
        <p className="error">{t('details.failed')}</p>
        <button
          type="button"
          onClick={() => {
            setTitle(null)
            setPhase('loading')
            setRetry((count) => count + 1)
          }}
        >
          {t('details.retry')}
        </button>
      </div>
    )
  }
  if (phase === 'loading' || !title) {
    return <p role="status">{t('details.loading')}</p>
  }

  const shown = title
  const state = request?.state ?? null
  const label: MessageKey = asking
    ? 'details.asking'
    : askable(state)
      ? 'details.ask'
      : (requestLabels[state ?? ''] ?? 'details.failedRequest')
  const seasons = title.seasons
  const selected =
    seasons.find((season) => season.number === picked) ??
    seasons.find((season) => season.number > 0) ??
    seasons[0] ??
    null

  async function ask() {
    setAsking(true)
    setAskError(null)
    try {
      const made = await requestTitle(shown.kind, shown.tmdbId)
      setRequest({ state: made.state, requestCount: made.requestCount })
    } catch (error) {
      const status = error instanceof ApiError ? error.status : 0
      setAskError(
        status === 422
          ? 'details.notAllowed'
          : status === 429
            ? 'details.allowance'
            : 'details.requestFailed',
      )
    } finally {
      setAsking(false)
    }
  }

  return (
    <article className="title-details">
      <p>
        <Link to="/home">{t('details.back')}</Link>
      </p>
      <h1>
        {title.title}
        {title.year ? ` (${title.year})` : ''}
      </h1>
      {title.logoUrl && (
        <img src={title.logoUrl} alt="" className="title-logo" />
      )}
      {title.trailerUrl ? (
        <PlexTrailer url={title.trailerUrl} backdropUrl={title.backdropUrl} />
      ) : (
        <Trailer
          key={`${title.kind}-${title.tmdbId}-${title.trailerKey ?? ''}`}
          videoId={title.trailerKey}
          backdropUrl={title.backdropUrl}
        />
      )}
      <div className="title-layout">
        <Artwork src={title.posterUrl} className="title-poster" />
        <div className="title-copy">
          <p className="title-meta">
            <span>
              {title.kind === 'movie' ? t('details.movie') : t('details.show')}
            </span>
            {title.runtimeMinutes !== null && (
              <span>
                {title.kind === 'show'
                  ? t('details.episodeRuntime', {
                      minutes: title.runtimeMinutes,
                    })
                  : t('details.runtime', { minutes: title.runtimeMinutes })}
              </span>
            )}
            {title.rating && <span className="badge">{title.rating}</span>}
            {title.needsParent && (
              <span className="badge badge-needsParent">
                {t('details.askParent')}
              </span>
            )}
            {title.availability && (
              <span className={`badge badge-${title.availability}`}>
                {title.availability === 'inLibrary'
                  ? t('details.inLibrary')
                  : t('details.notInLibrary')}
              </span>
            )}
          </p>
          {title.overview && <p className="title-overview">{title.overview}</p>}
          <button
            type="button"
            className="title-ask"
            disabled={asking || !askable(state)}
            onClick={() => void ask()}
          >
            {t(label)}
          </button>
          {askError && <p role="alert">{t(askError)}</p>}
        </div>
      </div>
      {title.kind === 'show' && (
        <section
          className="season-block"
          aria-labelledby={`${seasonGroup}-note`}
        >
          <h2 id={`${seasonGroup}-note`}>{t('details.seasons')}</h2>
          <p>{t('details.seasonNote')}</p>
          {seasons.length > 0 && (
            <fieldset className="seasons">
              <legend className="visually-hidden">
                {t('details.seasons')}
              </legend>
              {seasons.map((season) => (
                <label key={season.number} className="season-option">
                  <input
                    type="radio"
                    name={seasonGroup}
                    checked={selected?.number === season.number}
                    onChange={() => setPicked(season.number)}
                  />
                  {seasonName(season, t)}
                </label>
              ))}
            </fieldset>
          )}
          {selected && (
            <div className="season-panel">
              <Artwork src={selected.posterUrl} className="season-poster" />
              <h3>{seasonName(selected, t)}</h3>
              {selected.overview && <p>{selected.overview}</p>}
              <p>
                {selected.episodeCount === 1
                  ? t('details.episodeOne')
                  : t('details.episodes', { count: selected.episodeCount })}
              </p>
            </div>
          )}
        </section>
      )}
      {title.cast.length > 0 && (
        <section aria-labelledby={`${seasonGroup}-cast`}>
          <h2 id={`${seasonGroup}-cast`}>{t('details.cast')}</h2>
          <ul className="cast">
            {title.cast.map((member, index) => (
              <li key={`${member.name}-${index}`}>
                <Artwork src={member.profileUrl} className="cast-photo" />
                <span>{member.name}</span>
                {member.character && (
                  <span className="muted">{member.character}</span>
                )}
              </li>
            ))}
          </ul>
        </section>
      )}
    </article>
  )
}

export default function TitleDetails() {
  const rootRef = useRef<HTMLDivElement>(null)
  useKidSpatialNav(rootRef)
  const { kind, tmdbId } = useParams()
  return (
    <div ref={rootRef}>
      <TitleScreen key={`${kind ?? ''}:${tmdbId ?? ''}`} />
    </div>
  )
}
