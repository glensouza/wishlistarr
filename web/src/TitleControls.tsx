import { useEffect, useState } from 'react'
import { request } from './api'
import { contentControlError } from './contentControlError'
import { useT, type MessageKey } from './i18n'
import type { TitleRule } from './contentRules'

type Page = { total: number; items: TitleRule[] }
type Preview = {
  decision: 'allowed' | 'blocked' | 'needsApproval'
  reason: string
  reasonCode?: string
  reasonDetails?: {
    rating?: string
    genreId?: number
    keywordId?: number
    descriptor?: string
    topic?: string
  }
}

export default function TitleControls({
  profileId,
  disabled,
}: {
  profileId: number
  disabled: boolean
}) {
  const t = useT()
  const base = `/api/v1/kid-profiles/${profileId}`
  const [query, setQuery] = useState('')
  const [results, setResults] = useState<TitleRule[]>([])
  const [filter, setFilter] = useState('')
  const [debouncedFilter, setDebouncedFilter] = useState('')
  const [blocked, setBlocked] = useState(true)
  const [offset, setOffset] = useState(0)
  const [page, setPage] = useState<Page>({ total: 0, items: [] })
  const [revision, setRevision] = useState(0)
  const [preview, setPreview] = useState<Preview | null>(null)
  const [selected, setSelected] = useState<TitleRule | null>(null)
  const [error, setError] = useState<MessageKey | null>(null)
  const [busy, setBusy] = useState(false)
  useEffect(() => {
    const timer = setTimeout(() => setDebouncedFilter(filter), 300)
    return () => clearTimeout(timer)
  }, [filter])
  useEffect(() => {
    if (filter !== debouncedFilter) return
    const controller = new AbortController()
    void request<Page>(
      'GET',
      `${base}/titles?blocked=${blocked}&q=${encodeURIComponent(debouncedFilter)}&limit=20&offset=${offset}`,
      undefined,
      controller.signal,
    ).then(
      (result) => {
        if (!controller.signal.aborted) setPage(result)
      },
      (error: unknown) => {
        if (!controller.signal.aborted) setError(contentControlError(error))
      },
    )
    return () => controller.abort()
  }, [base, blocked, debouncedFilter, filter, offset, revision])
  useEffect(() => {
    if (!selected) return
    const controller = new AbortController()
    void request<Preview>(
      'POST',
      `${base}/preview`,
      selected,
      controller.signal,
    ).then(
      (result) => {
        if (!controller.signal.aborted) setPreview(result)
      },
      (error: unknown) => {
        if (!controller.signal.aborted) setError(contentControlError(error))
      },
    )
    return () => controller.abort()
  }, [base, selected, revision, disabled])
  async function search() {
    setError(null)
    setBusy(true)
    try {
      setResults(
        await request<TitleRule[]>(
          'GET',
          `/api/v1/kid-profiles/title-search?q=${encodeURIComponent(query)}`,
        ),
      )
    } catch (error) {
      setError(contentControlError(error))
    } finally {
      setBusy(false)
    }
  }
  async function mutate(row: TitleRule, remove: boolean) {
    setBusy(true)
    setError(null)
    try {
      await request<void>(
        remove ? 'DELETE' : 'POST',
        remove
          ? `${base}/titles/${row.kind}/${row.tmdbId}?blocked=${row.blocked}`
          : `${base}/titles`,
        remove ? undefined : row,
      )
      setPreview(null)
      setRevision((value) => value + 1)
    } catch (error) {
      setError(contentControlError(error))
    } finally {
      setBusy(false)
    }
  }
  return (
    <fieldset disabled={disabled || busy}>
      <legend>{t('controls.titles')}</legend>
      <form
        onSubmit={(event) => {
          event.preventDefault()
          if (query.trim().length >= 2) void search()
        }}
      >
        <label>
          {t('controls.titleSearch')}
          <input
            type="search"
            maxLength={100}
            value={query}
            onChange={(event) => setQuery(event.target.value)}
          />
        </label>
        <button type="submit" disabled={query.trim().length < 2}>
          {t('controls.search')}
        </button>
      </form>
      <ul>
        {results.map((row) => (
          <li key={`${row.kind}:${row.tmdbId}`}>
            <button
              type="button"
              onClick={() => {
                setPreview(null)
                setSelected(row)
              }}
            >
              {row.title} (
              {t(row.kind === 'movie' ? 'controls.movie' : 'controls.show')})
            </button>
          </li>
        ))}
      </ul>
      {selected && (
        <div>
          <h3>
            {t('controls.preview')}: {selected.title}
          </h3>
          <p>{t('controls.previewNote')}</p>
          {preview && (
            <p role="status">
              {t(`controls.decision.${preview.decision}`)}:{' '}
              {preview.reasonCode
                ? t(`controls.reason.${preview.reasonCode}` as MessageKey, {
                    rating: preview.reasonDetails?.rating ?? '',
                    genre: String(preview.reasonDetails?.genreId ?? ''),
                    keyword: String(preview.reasonDetails?.keywordId ?? ''),
                    descriptor: preview.reasonDetails?.descriptor ?? '',
                    topic: preview.reasonDetails?.topic ?? '',
                  })
                : preview.reason}
            </p>
          )}
          <button
            type="button"
            onClick={() => void mutate({ ...selected, blocked: true }, false)}
          >
            {t('controls.block')}
          </button>
          <button
            type="button"
            onClick={() => void mutate({ ...selected, blocked: false }, false)}
          >
            {t('controls.allow')}
          </button>
        </div>
      )}
      <label>
        {t('controls.list')}
        <select
          value={String(blocked)}
          onChange={(event) => {
            setBlocked(event.target.value === 'true')
            setOffset(0)
          }}
        >
          <option value="true">{t('controls.blocked')}</option>
          <option value="false">{t('controls.allowed')}</option>
        </select>
      </label>
      <label>
        {t('controls.filterTitles')}
        <input
          type="search"
          maxLength={100}
          value={filter}
          onChange={(event) => {
            setFilter(event.target.value)
            setOffset(0)
          }}
        />
      </label>
      <ul>
        {page.items.map((row) => (
          <li key={`${row.kind}:${row.tmdbId}`}>
            {row.title} (
            {t(row.kind === 'movie' ? 'controls.movie' : 'controls.show')}){' '}
            <button
              type="button"
              aria-label={t('controls.removeTitle', { title: row.title })}
              onClick={() => void mutate(row, true)}
            >
              {t('controls.remove')}
            </button>
          </li>
        ))}
      </ul>
      <button
        type="button"
        disabled={offset === 0}
        onClick={() => setOffset(Math.max(0, offset - 20))}
      >
        {t('controls.previous')}
      </button>
      <button
        type="button"
        disabled={offset + 20 >= page.total}
        onClick={() => setOffset(offset + 20)}
      >
        {t('controls.next')}
      </button>
      {error && (
        <p role="alert">
          {t(error)}{' '}
          <button
            type="button"
            onClick={() => {
              setError(null)
              setRevision((value) => value + 1)
            }}
          >
            {t('controls.retry')}
          </button>
        </p>
      )}
    </fieldset>
  )
}
