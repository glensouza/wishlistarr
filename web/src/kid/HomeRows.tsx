import { useEffect, useId, useState, type KeyboardEvent } from 'react'
import { useT } from '../i18n'
import { getHome, requestTitle, type HomeItem, type HomeRow } from './kidApi'

const animationGenreId = 16
const familyGenreId = 10751
const comedyGenreId = 35

export default function HomeRows() {
  const t = useT()
  const [rows, setRows] = useState<HomeRow[] | null>(null)
  const [failed, setFailed] = useState(false)
  const [retry, setRetry] = useState(0)

  useEffect(() => {
    const controller = new AbortController()
    getHome(controller.signal).then(
      (loaded) => {
        if (controller.signal.aborted) return
        setRows(loaded.rows.filter((row) => row.items.length > 0))
      },
      () => {
        if (controller.signal.aborted) return
        setFailed(true)
      },
    )
    return () => controller.abort()
  }, [retry])

  function rowTitle(row: HomeRow) {
    if (row.id === 'popular') return t('home.rowPopular')
    if (row.id === 'library') return t('home.rowLibrary')
    if (row.genreId === animationGenreId) return t('home.genreAnimation')
    if (row.genreId === familyGenreId) return t('home.genreFamily')
    if (row.genreId === comedyGenreId) return t('home.genreComedy')
    return t('home.genreOther', { id: row.genreId ?? 0 })
  }

  function onKeyDown(event: KeyboardEvent<HTMLElement>) {
    if (
      event.key !== 'ArrowLeft' &&
      event.key !== 'ArrowRight' &&
      event.key !== 'ArrowUp' &&
      event.key !== 'ArrowDown'
    ) {
      return
    }
    const target = event.target
    if (!(target instanceof HTMLElement) || target.closest('input, textarea')) {
      return
    }
    const current = target.closest<HTMLElement>('.home-ask')
    const row = target.closest<HTMLElement>('[data-home-row]')
    const container = event.currentTarget
    if (!current || !row) return
    const rowsInOrder = [
      ...container.querySelectorAll<HTMLElement>('[data-home-row]'),
    ]
    const inRow = [...row.querySelectorAll<HTMLElement>('.home-ask')]
    const rowIndex = rowsInOrder.indexOf(row)
    const indexInRow = inRow.indexOf(current)
    let next: HTMLElement | null = null
    if (event.key === 'ArrowRight') next = inRow[indexInRow + 1] ?? null
    if (event.key === 'ArrowLeft') next = inRow[indexInRow - 1] ?? null
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      const destination =
        rowsInOrder[rowIndex + (event.key === 'ArrowDown' ? 1 : -1)]
      if (destination) {
        const buttons = [
          ...destination.querySelectorAll<HTMLElement>('.home-ask'),
        ]
        next = buttons[Math.min(indexInRow, buttons.length - 1)] ?? null
      }
    }
    if (!next) return
    event.preventDefault()
    next.focus()
    if (typeof next.scrollIntoView === 'function') {
      next.scrollIntoView({ block: 'nearest', inline: 'nearest' })
    }
  }

  if (failed) {
    return (
      <div role="alert">
        <p className="error">{t('home.failed')}</p>
        <button
          type="button"
          onClick={() => {
            setFailed(false)
            setRows(null)
            setRetry((count) => count + 1)
          }}
        >
          {t('search.retry')}
        </button>
      </div>
    )
  }

  if (rows === null) {
    return <p role="status">{t('home.loading')}</p>
  }

  if (rows.length === 0) {
    return <p>{t('home.empty')}</p>
  }

  return (
    <div className="home-rows" onKeyDown={onKeyDown}>
      {rows.map((row) => (
        <section key={row.id} className="home-row" data-home-row>
          <h2>{rowTitle(row)}</h2>
          <ul className="home-strip">
            {row.items.map((item) => (
              <HomeCard
                key={`${row.id}:${item.kind}:${item.tmdbId}`}
                item={item}
              />
            ))}
          </ul>
        </section>
      ))}
    </div>
  )
}

function HomeCard({ item }: { item: HomeItem }) {
  const t = useT()
  const titleId = useId()
  const [posterFailed, setPosterFailed] = useState(false)
  const [busy, setBusy] = useState(false)
  const [note, setNote] = useState<string | null>(null)
  const showPoster = item.posterUrl !== null && !posterFailed

  async function ask() {
    setBusy(true)
    setNote(null)
    try {
      await requestTitle(item.kind, item.tmdbId)
      setNote(t('search.requested'))
    } catch {
      setNote(t('search.requestFailed'))
      setBusy(false)
    }
  }

  return (
    <li className="home-card">
      <div className="poster-frame">
        {showPoster ? (
          <img
            src={item.posterUrl ?? undefined}
            alt=""
            loading="lazy"
            onError={() => setPosterFailed(true)}
          />
        ) : (
          <div
            className="poster-fallback"
            role="img"
            aria-label={t('home.noPoster', { title: item.title })}
          />
        )}
      </div>
      <h3 id={titleId}>{item.title}</h3>
      <p className="muted">
        {item.year ? `${item.year} · ` : ''}
        {item.kind === 'movie' ? t('search.movie') : t('search.show')}
      </p>
      {item.availability === 'inLibrary' && (
        <span className="badge badge-inLibrary">
          {t('home.libraryBadge', { title: item.title })}
        </span>
      )}
      <button
        type="button"
        className="home-ask"
        disabled={busy}
        aria-label={t('search.ask', { title: item.title })}
        onClick={() => void ask()}
      >
        {busy ? t('search.asking') : t('search.askShort')}
      </button>
      {note && <p role="status">{note}</p>}
    </li>
  )
}
