import { useEffect, useRef, useState } from 'react'
import { ApiError, request } from './api'
import {
  listQueue,
  QueueShapeError,
  reasonText,
  type QueueGap,
  type QueueItem,
  type QueuePage,
} from './approvalQueue'
import { WatchProviders } from './about/WatchProviders'
import { useT, type MessageKey } from './i18n'
import Trailer, { isYoutubeId } from './kid/Trailer'
import { ProfileEvents } from './RequestHistoryPanel'
import {
  approveRequests,
  decisionResultKey,
  denyRequests,
  readDecision,
} from './requests'

const maxNote = 500

const quickActions = [
  ['#requests-heading', 'queue.actionRequests'],
  ['#kid-profiles-heading', 'queue.actionProfiles'],
  ['#kid-devices-heading', 'queue.actionDevices'],
  ['#request-history-heading', 'queue.actionHistory'],
  ['#backups-heading', 'queue.actionBackups'],
  ['#logs-heading', 'queue.actionLogs'],
  ['#trailers-heading', 'queue.actionTrailers'],
] as const

type Kid = { id: number; name: string }

type QueueLoad =
  | { kind: 'loading' }
  | { kind: 'ready'; page: QueuePage }
  | { kind: 'error'; error: unknown }

type ProfileLoad =
  { kind: 'loading' } | { kind: 'ready'; profiles: Kid[] } | { kind: 'error' }

type Notice = { kind: 'status' | 'error'; text: string }

function isKid(value: unknown): value is Kid {
  if (typeof value !== 'object' || value === null) return false
  const profile = value as Record<string, unknown>
  return typeof profile.id === 'number' && typeof profile.name === 'string'
}

function displayTitle(item: QueueItem, t: (key: MessageKey) => string) {
  const title = item.title.trim()
  return title === '' ? t('queue.untitled') : title
}

function loadError(error: unknown, t: (key: MessageKey) => string) {
  if (error instanceof QueueShapeError) return t('queue.shape')
  if (error instanceof ApiError) return error.message
  return t('queue.loadFailed')
}

function kidOptions(profiles: Kid[], items: QueueItem[], held: Kid | null) {
  const names = new Map<number, string>()
  if (held) names.set(held.id, held.name)
  for (const item of items) {
    if (!names.has(item.kidProfileId))
      names.set(item.kidProfileId, item.kidName)
  }
  for (const profile of profiles) names.set(profile.id, profile.name)
  return [...names.entries()]
    .map(([id, name]) => ({ id, name }))
    .sort((left, right) =>
      left.name.localeCompare(right.name, undefined, { sensitivity: 'base' }),
    )
}

function kidId(filter: string) {
  if (filter === '') return undefined
  const id = Number(filter)
  if (!Number.isInteger(id) || id < 1) return undefined
  return id
}

function nextQueueFocus(
  listed: QueueItem[],
  decidedId: number,
  busy: ReadonlySet<number>,
) {
  const ready = (row: QueueItem) => row.id !== decidedId && !busy.has(row.id)
  const index = listed.findIndex((row) => row.id === decidedId)
  const after = index < 0 ? listed : listed.slice(index + 1)
  const before = index < 0 ? [] : listed.slice(0, index)
  const next = after.find(ready) ?? [...before].reverse().find(ready)
  if (next) return `queue-approve-${next.id}`
  const stillThere = listed.some((row) => row.id !== decidedId)
  return stillThere ? 'queue-kid' : 'queue-empty'
}

function restingQueue(page: QueuePage) {
  if (page.items.length > 0) return 'cards' as const
  return page.total > 0 ? ('more' as const) : ('empty' as const)
}

function omitDecided(page: QueuePage, decided: ReadonlySet<number>): QueuePage {
  const items = page.items.filter((item) => !decided.has(item.id))
  if (items.length === page.items.length) return page
  const removed = page.items.length - items.length
  return {
    ...page,
    items,
    total: Math.max(items.length, page.total - removed),
  }
}

function claimNextPage(
  loadingMore: { current: boolean },
  refillFocus: { current: boolean },
) {
  if (loadingMore.current) return false
  loadingMore.current = true
  refillFocus.current = true
  return true
}

function rememberDecision(
  decided: Set<number>,
  id: number,
  page: QueuePage | null,
  loadingMore: { current: boolean },
  refillFocus: { current: boolean },
) {
  decided.add(id)
  if (page === null || !page.items.some((row) => row.id === id)) {
    return { next: null as QueuePage | null, reload: false }
  }
  const next = omitDecided(page, decided)
  const reload =
    restingQueue(next) === 'more' && claimNextPage(loadingMore, refillFocus)
  return { next, reload }
}

function decisionFocus(
  before: QueueItem[],
  decidedId: number,
  busy: ReadonlySet<number>,
  after: QueuePage,
) {
  const rest = restingQueue(after)
  if (rest === 'cards') return nextQueueFocus(before, decidedId, busy)
  return rest === 'more' ? 'queue-more' : 'queue-empty'
}

function settleArrival(
  arrived: QueuePage,
  decided: ReadonlySet<number>,
  loadingMore: { current: boolean },
  refillFocus: { current: boolean },
) {
  const next = omitDecided(arrived, decided)
  const dropped = next.items.length !== arrived.items.length
  if (
    dropped &&
    restingQueue(next) === 'more' &&
    claimNextPage(loadingMore, refillFocus)
  ) {
    return { kind: 'reload' as const }
  }
  loadingMore.current = false
  const focusArrived = refillFocus.current
  refillFocus.current = false
  if (!focusArrived)
    return { kind: 'ready' as const, page: next, focus: 'none' as const }
  if (next.items.length > 0) {
    return { kind: 'ready' as const, page: next, focus: 'item' as const }
  }
  return {
    kind: 'ready' as const,
    page: next,
    focus:
      restingQueue(next) === 'more' ? ('more' as const) : ('empty' as const),
  }
}

export default function ParentHome() {
  const t = useT()
  const inFlight = useRef(new Set<number>())
  const [attempt, setAttempt] = useState(0)
  const [queueAttempt, setQueueAttempt] = useState(0)
  const [queueLoad, setQueueLoad] = useState<QueueLoad>({ kind: 'loading' })
  const [profileLoad, setProfileLoad] = useState<ProfileLoad>({
    kind: 'loading',
  })
  const [filter, setFilter] = useState('')
  const [heldKid, setHeldKid] = useState<Kid | null>(null)
  const [tmdbReady, setTmdbReady] = useState(true)
  const [notes, setNotes] = useState<Record<number, string>>({})
  const [noteErrors, setNoteErrors] = useState<Record<number, string>>({})
  const [busy, setBusy] = useState<number[]>([])
  const [notice, setNotice] = useState<Notice | null>(null)
  const pendingFocus = useRef<string | null>(null)
  const [focusTick, setFocusTick] = useState(0)
  const queueRef = useRef<QueueLoad>({ kind: 'loading' })
  const decidedIds = useRef(new Set<number>())
  const loadingMore = useRef(false)
  const refillFocus = useRef(false)

  function focusOn(id: string) {
    pendingFocus.current = id
    setFocusTick((tick) => tick + 1)
  }

  function showQueue(next: QueueLoad) {
    queueRef.current = next
    setQueueLoad(next)
  }

  function retry() {
    loadingMore.current = false
    refillFocus.current = false
    showQueue({ kind: 'loading' })
    setProfileLoad({ kind: 'loading' })
    setAttempt((count) => count + 1)
  }

  function beginNextPage() {
    if (!claimNextPage(loadingMore, refillFocus)) return false
    showQueue({ kind: 'loading' })
    setQueueAttempt((count) => count + 1)
    focusOn('queue-loading')
    return true
  }

  function loadMore() {
    loadingMore.current = false
    beginNextPage()
  }

  useEffect(() => {
    let current = true
    listQueue(kidId(filter)).then(
      (arrived) => {
        if (!current) return
        const settled = settleArrival(
          arrived,
          decidedIds.current,
          loadingMore,
          refillFocus,
        )
        if (settled.kind === 'reload') {
          queueRef.current = { kind: 'loading' }
          setQueueLoad(queueRef.current)
          setQueueAttempt((count) => count + 1)
          pendingFocus.current = 'queue-loading'
          setFocusTick((tick) => tick + 1)
          return
        }
        setTmdbReady(settled.page.tmdbConfigured)
        queueRef.current = { kind: 'ready', page: settled.page }
        setQueueLoad(queueRef.current)
        if (settled.focus === 'none') return
        pendingFocus.current =
          settled.focus === 'item'
            ? `queue-approve-${settled.page.items[0].id}`
            : settled.focus === 'more'
              ? 'queue-more'
              : 'queue-empty'
        setFocusTick((tick) => tick + 1)
      },
      (error: unknown) => {
        if (!current) return
        loadingMore.current = false
        const focusArrived = refillFocus.current
        refillFocus.current = false
        queueRef.current = { kind: 'error', error }
        setQueueLoad(queueRef.current)
        if (!focusArrived) return
        pendingFocus.current = 'queue-load-error'
        setFocusTick((tick) => tick + 1)
      },
    )
    return () => {
      current = false
    }
  }, [attempt, queueAttempt, filter])

  useEffect(() => {
    let current = true
    request<unknown>('GET', '/api/v1/kid-profiles').then(
      (body) => {
        if (!current) return
        if (!Array.isArray(body) || !body.every(isKid)) {
          setProfileLoad({ kind: 'error' })
          return
        }
        setProfileLoad({ kind: 'ready', profiles: body })
      },
      () => {
        if (current) setProfileLoad({ kind: 'error' })
      },
    )
    return () => {
      current = false
    }
  }, [attempt])

  useEffect(() => {
    if (focusTick === 0) return
    const id = pendingFocus.current
    if (id) document.getElementById(id)?.focus()
  }, [focusTick])

  const page = queueLoad.kind === 'ready' ? queueLoad.page : null
  const profiles = profileLoad.kind === 'ready' ? profileLoad.profiles : []
  const held = heldKid && String(heldKid.id) === filter ? heldKid : null
  const options = kidOptions(profiles, page?.items ?? [], held)
  const selected = options.find((kid) => String(kid.id) === filter) ?? null
  const activity =
    filter === '' ? options : options.filter((kid) => String(kid.id) === filter)
  const tmdbOff = page ? !page.tmdbConfigured : !tmdbReady
  const rest = page === null ? null : restingQueue(page)

  function chooseFilter(value: string) {
    loadingMore.current = false
    refillFocus.current = false
    setFilter(value)
    if (value === '') {
      setHeldKid(null)
    } else {
      const known = options.find((kid) => String(kid.id) === value)
      if (known) setHeldKid(known)
    }
    showQueue({ kind: 'loading' })
  }

  async function decide(item: QueueItem, approving: boolean) {
    if (inFlight.current.has(item.id) || queueRef.current.kind !== 'ready')
      return
    const title = displayTitle(item, t)
    const note = notes[item.id] ?? ''
    if (!approving && note.trim().length > maxNote) {
      setNoteErrors((current) => ({
        ...current,
        [item.id]: t('queue.noteTooLong'),
      }))
      focusOn(`queue-note-error-${item.id}`)
      return
    }

    inFlight.current.add(item.id)
    setBusy((current) =>
      current.includes(item.id) ? current : [...current, item.id],
    )
    setNoteErrors((current) => {
      if (!(item.id in current)) return current
      const next = { ...current }
      delete next[item.id]
      return next
    })
    try {
      const body = approving
        ? await approveRequests([item.id], null)
        : await denyRequests([item.id], note.trim() === '' ? null : note.trim())
      const decision = readDecision(body, item.id)
      if (!decision) {
        setNotice({ kind: 'error', text: t('queue.resultUnknown') })
        focusOn('queue-decision-error')
        return
      }
      if (
        decision.status === 'succeeded' ||
        decision.status === 'alreadyDecided'
      ) {
        setNotice({
          kind: 'status',
          text:
            decision.status === 'succeeded'
              ? t(approving ? 'queue.approved' : 'queue.denied', { title })
              : t(decisionResultKey.alreadyDecided),
        })
        const ready =
          queueRef.current.kind === 'ready' ? queueRef.current.page : null
        const settled = rememberDecision(
          decidedIds.current,
          item.id,
          ready,
          loadingMore,
          refillFocus,
        )
        if (settled.reload) {
          showQueue({ kind: 'loading' })
          setQueueAttempt((count) => count + 1)
          focusOn('queue-loading')
          return
        }
        if (settled.next && ready) {
          showQueue({ kind: 'ready', page: settled.next })
          focusOn(
            decisionFocus(ready.items, item.id, inFlight.current, settled.next),
          )
          return
        }
        focusOn('queue-kid')
        return
      }
      setNotice({
        kind: 'error',
        text: t(decisionResultKey[decision.status]),
      })
      focusOn('queue-decision-error')
    } catch (failure) {
      setNotice({
        kind: 'error',
        text:
          failure instanceof ApiError ? failure.message : t('queue.sendFailed'),
      })
      focusOn('queue-decision-error')
    } finally {
      inFlight.current.delete(item.id)
      setBusy((current) => current.filter((id) => id !== item.id))
    }
  }

  return (
    <div className="parent-home">
      <nav className="quick-actions" aria-label={t('queue.actions')}>
        {quickActions.map(([href, key]) => (
          <a key={href} href={href}>
            {t(key)}
          </a>
        ))}
      </nav>
      <section aria-labelledby="approval-queue-heading">
        <h2 id="approval-queue-heading">{t('queue.heading')}</h2>
        {tmdbOff && <p role="status">{t('queue.tmdbOff')}</p>}
        <label htmlFor="queue-kid">{t('queue.filter')}</label>
        <select
          id="queue-kid"
          value={filter}
          onChange={(event) => chooseFilter(event.target.value)}
        >
          <option value="">{t('queue.filterAll')}</option>
          {options.map((kid) => (
            <option key={kid.id} value={kid.id}>
              {kid.name}
            </option>
          ))}
        </select>
        {notice?.kind === 'status' && <p role="status">{notice.text}</p>}
        {notice?.kind === 'error' && (
          <p id="queue-decision-error" role="alert" tabIndex={-1}>
            {notice.text}
          </p>
        )}
        {queueLoad.kind === 'loading' && (
          <p id="queue-loading" role="status" tabIndex={-1}>
            {t('queue.loading')}
          </p>
        )}
        {queueLoad.kind === 'error' && (
          <div id="queue-load-error" role="alert" tabIndex={-1}>
            <p>{loadError(queueLoad.error, t)}</p>
            <button type="button" onClick={retry}>
              {t('queue.retry')}
            </button>
          </div>
        )}
        {page !== null &&
          rest === 'cards' &&
          page.total > page.items.length && (
            <p>
              {t('queue.more', { count: page.total - page.items.length })}{' '}
              <a href="#requests-heading">{t('queue.actionRequests')}</a>
            </p>
          )}
        {page !== null && rest === 'more' && (
          <div>
            <p>{t('queue.moreWaiting', { count: page.total })}</p>
            <button type="button" id="queue-more" onClick={loadMore}>
              {t('queue.loadMore')}
            </button>
          </div>
        )}
        {page !== null && rest === 'empty' && (
          <p id="queue-empty" tabIndex={-1}>
            {selected
              ? t('queue.emptyKid', { name: selected.name })
              : t('queue.empty')}
          </p>
        )}
        {page !== null && rest === 'cards' && (
          <div className="approval-queue">
            {page.items.map((item) => (
              <QueueCard
                key={item.id}
                item={item}
                tmdbConfigured={page.tmdbConfigured}
                note={notes[item.id] ?? ''}
                noteError={noteErrors[item.id] ?? null}
                waiting={busy.includes(item.id)}
                onNote={(value) =>
                  setNotes((current) => ({ ...current, [item.id]: value }))
                }
                onApprove={() => void decide(item, true)}
                onDeny={() => void decide(item, false)}
              />
            ))}
          </div>
        )}
      </section>
      <section aria-labelledby="recent-activity-heading">
        <h2 id="recent-activity-heading">{t('queue.activity')}</h2>
        {profileLoad.kind === 'error' && (
          <p role="alert">{t('queue.profilesFailed')}</p>
        )}
        {profileLoad.kind === 'ready' &&
          queueLoad.kind !== 'loading' &&
          options.length === 0 && <p>{t('history.none')}</p>}
        {activity.map((kid) => (
          <ProfileEvents
            key={kid.id}
            profile={kid}
            limit={5}
            listLabel={t('queue.activityFor', { name: kid.name })}
            errorText={t('queue.activityFailed', { name: kid.name })}
          />
        ))}
      </section>
    </div>
  )
}

function QueueCard({
  item,
  tmdbConfigured,
  note,
  noteError,
  waiting,
  onNote,
  onApprove,
  onDeny,
}: {
  item: QueueItem
  tmdbConfigured: boolean
  note: string
  noteError: string | null
  waiting: boolean
  onNote: (value: string) => void
  onApprove: () => void
  onDeny: () => void
}) {
  const t = useT()
  const title = displayTitle(item, t)
  const hide = (gap: QueueGap) => !tmdbConfigured && item.gaps.includes(gap)
  const names = item.providers
    .map((provider) => provider.name.trim())
    .filter((name) => name.length > 0)
  return (
    <article
      className="approval-card"
      aria-labelledby={`queue-title-${item.id}`}
    >
      <h3 id={`queue-title-${item.id}`}>{title}</h3>
      <p>
        {t('requests.forKid', {
          kind: t(item.kind === 'movie' ? 'requests.movie' : 'requests.show'),
          name: item.kidName,
        })}
      </p>
      {item.requestCount > 1 && (
        <p>{t('queue.askedAgain', { count: item.requestCount })}</p>
      )}
      <p>{reasonText(item.reason, t)}</p>
      {!hide('keywords') && item.gaps.includes('keywords') && (
        <p>{t('queue.keywordsFailed')}</p>
      )}
      {!hide('synopsis') && (
        <p>
          <span className="muted">{t('queue.synopsis')}</span>{' '}
          {item.gaps.includes('synopsis')
            ? t('queue.synopsisFailed')
            : item.overview && item.overview.trim() !== ''
              ? item.overview
              : t('queue.synopsisNone')}
        </p>
      )}
      {!hide('rating') && (
        <p>
          {item.gaps.includes('rating')
            ? t('queue.ratingFailed')
            : item.rating && item.rating.trim() !== ''
              ? t('queue.ratingValue', { rating: item.rating })
              : t('queue.ratingNone')}
        </p>
      )}
      {!hide('rating') &&
        (item.descriptors.length > 0 ? (
          <ul aria-label={t('queue.descriptors')}>
            {item.descriptors.map((descriptor) => (
              <li key={descriptor}>{descriptor}</li>
            ))}
          </ul>
        ) : (
          <p>{t('queue.descriptorsNone')}</p>
        ))}
      {!hide('providers') &&
        (item.gaps.includes('providers') ? (
          <p>{t('queue.providersFailed')}</p>
        ) : names.length === 0 ? (
          <p>{t('queue.providersNone')}</p>
        ) : (
          <WatchProviders providers={item.providers} />
        ))}
      {!hide('trailer') &&
        (item.gaps.includes('trailer') ? (
          <p>{t('queue.trailerFailed')}</p>
        ) : (
          isYoutubeId(item.trailerKey) && (
            <Trailer videoId={item.trailerKey} backdropUrl={null} />
          )
        ))}
      <label htmlFor={`queue-note-${item.id}`}>
        {t('queue.note', { name: item.kidName })}
      </label>
      <textarea
        id={`queue-note-${item.id}`}
        value={note}
        onChange={(event) => onNote(event.target.value)}
      />
      <p className="muted">{t('queue.noteHint')}</p>
      {noteError && (
        <p id={`queue-note-error-${item.id}`} role="alert" tabIndex={-1}>
          {noteError}
        </p>
      )}
      {item.kind === 'show' && (
        <>
          <p>{t('requests.showAccess')}</p>
          {item.seasons?.map((season) => (
            <p key={season.number}>
              {t('details.seasonName', { number: season.number })}
            </p>
          ))}
        </>
      )}
      <div className="approval-actions">
        <button
          type="button"
          id={`queue-approve-${item.id}`}
          disabled={waiting}
          onClick={onApprove}
        >
          {t('queue.approve', { title, name: item.kidName })}
        </button>
        <button
          type="button"
          className="button-deny"
          disabled={waiting}
          onClick={onDeny}
        >
          {t('queue.deny', { title, name: item.kidName })}
        </button>
      </div>
    </article>
  )
}
