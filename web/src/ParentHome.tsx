import { useEffect, useRef, useState } from 'react'
import { ApiError, request } from './api'
import {
  listQueue,
  QueueShapeError,
  reasonText,
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

function kidOptions(profiles: Kid[], items: QueueItem[]) {
  const names = new Map<number, string>()
  for (const profile of profiles) names.set(profile.id, profile.name)
  for (const item of items) {
    if (!names.has(item.kidProfileId))
      names.set(item.kidProfileId, item.kidName)
  }
  return [...names.entries()]
    .map(([id, name]) => ({ id, name }))
    .sort((left, right) =>
      left.name.localeCompare(right.name, undefined, { sensitivity: 'base' }),
    )
}

function visibleItems(page: QueuePage, filter: string) {
  return page.items.filter(
    (item) => filter === '' || String(item.kidProfileId) === filter,
  )
}

export default function ParentHome() {
  const t = useT()
  const inFlight = useRef(new Set<number>())
  const [attempt, setAttempt] = useState(0)
  const [queueLoad, setQueueLoad] = useState<QueueLoad>({ kind: 'loading' })
  const [profileLoad, setProfileLoad] = useState<ProfileLoad>({
    kind: 'loading',
  })
  const [filter, setFilter] = useState('')
  const [notes, setNotes] = useState<Record<number, string>>({})
  const [noteErrors, setNoteErrors] = useState<Record<number, string>>({})
  const [busy, setBusy] = useState<number[]>([])
  const [notice, setNotice] = useState<Notice | null>(null)
  const pendingFocus = useRef<string | null>(null)
  const [focusTick, setFocusTick] = useState(0)

  function focusOn(id: string) {
    pendingFocus.current = id
    setFocusTick((tick) => tick + 1)
  }

  function retry() {
    setQueueLoad({ kind: 'loading' })
    setProfileLoad({ kind: 'loading' })
    setAttempt((count) => count + 1)
  }

  useEffect(() => {
    let current = true
    listQueue().then(
      (page) => {
        if (current) setQueueLoad({ kind: 'ready', page })
      },
      (error: unknown) => {
        if (current) setQueueLoad({ kind: 'error', error })
      },
    )
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
  const options = kidOptions(profiles, page?.items ?? [])
  const shown = page === null ? [] : visibleItems(page, filter)
  const selected = options.find((kid) => String(kid.id) === filter) ?? null
  const activity =
    filter === '' ? options : options.filter((kid) => String(kid.id) === filter)

  async function decide(item: QueueItem, approving: boolean) {
    if (inFlight.current.has(item.id) || page === null) return
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
        const listed = visibleItems(page, filter)
        const index = listed.findIndex((row) => row.id === item.id)
        const remaining = listed.filter((row) => row.id !== item.id)
        const next = remaining[index] ?? remaining[index - 1]
        setNotice({
          kind: 'status',
          text:
            decision.status === 'succeeded'
              ? t(approving ? 'queue.approved' : 'queue.denied', { title })
              : t(decisionResultKey.alreadyDecided),
        })
        focusOn(next ? `queue-approve-${next.id}` : 'queue-empty')
        setQueueLoad((current) =>
          current.kind === 'ready'
            ? {
                kind: 'ready',
                page: {
                  ...current.page,
                  total: Math.max(0, current.page.total - 1),
                  items: current.page.items.filter((row) => row.id !== item.id),
                },
              }
            : current,
        )
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
        <label htmlFor="queue-kid">{t('queue.filter')}</label>
        <select
          id="queue-kid"
          value={filter}
          onChange={(event) => setFilter(event.target.value)}
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
          <p role="status">{t('queue.loading')}</p>
        )}
        {queueLoad.kind === 'error' && (
          <div role="alert">
            <p>{loadError(queueLoad.error, t)}</p>
            <button type="button" onClick={retry}>
              {t('queue.retry')}
            </button>
          </div>
        )}
        {page !== null && page.total > page.items.length && (
          <p>
            {t('queue.more', { count: page.total - page.items.length })}{' '}
            <a href="#requests-heading">{t('queue.actionRequests')}</a>
          </p>
        )}
        {page !== null && shown.length === 0 && (
          <p id="queue-empty" tabIndex={-1}>
            {selected
              ? t('queue.emptyKid', { name: selected.name })
              : t('queue.empty')}
          </p>
        )}
        {page !== null && shown.length > 0 && (
          <div className="approval-queue">
            {shown.map((item) => (
              <QueueCard
                key={item.id}
                item={item}
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
  note,
  noteError,
  waiting,
  onNote,
  onApprove,
  onDeny,
}: {
  item: QueueItem
  note: string
  noteError: string | null
  waiting: boolean
  onNote: (value: string) => void
  onApprove: () => void
  onDeny: () => void
}) {
  const t = useT()
  const title = displayTitle(item, t)
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
      <p>
        <span className="muted">{t('queue.synopsis')}</span>{' '}
        {item.gaps.includes('synopsis')
          ? t('queue.synopsisFailed')
          : item.overview && item.overview.trim() !== ''
            ? item.overview
            : t('queue.synopsisNone')}
      </p>
      <p>
        {item.gaps.includes('rating')
          ? t('queue.ratingFailed')
          : item.rating && item.rating.trim() !== ''
            ? t('queue.ratingValue', { rating: item.rating })
            : t('queue.ratingNone')}
      </p>
      {item.descriptors.length > 0 ? (
        <ul aria-label={t('queue.descriptors')}>
          {item.descriptors.map((descriptor) => (
            <li key={descriptor}>{descriptor}</li>
          ))}
        </ul>
      ) : (
        <p>{t('queue.descriptorsNone')}</p>
      )}
      {item.gaps.includes('providers') ? (
        <p>{t('queue.providersFailed')}</p>
      ) : names.length === 0 ? (
        <p>{t('queue.providersNone')}</p>
      ) : (
        <WatchProviders providers={item.providers} />
      )}
      {item.gaps.includes('trailer') ? (
        <p>{t('queue.trailerFailed')}</p>
      ) : (
        isYoutubeId(item.trailerKey) && (
          <Trailer videoId={item.trailerKey} backdropUrl={null} />
        )
      )}
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
