import { useEffect, useState } from 'react'
import { request } from './api'
import { useT, type MessageKey } from './i18n'

type ProfileRef = { id: number; name: string }

type RequestEvent = {
  id: number
  requestId: number
  title: string | null
  fromState: string | null
  toState: string
  actorName: string
  reason: string | null
  occurredAt: string
}

function isProfile(value: unknown): value is ProfileRef {
  if (typeof value !== 'object' || value === null) return false
  const profile = value as Record<string, unknown>
  return typeof profile.id === 'number' && typeof profile.name === 'string'
}

function isEvent(value: unknown): value is RequestEvent {
  if (typeof value !== 'object' || value === null) return false
  const item = value as Record<string, unknown>
  return (
    typeof item.id === 'number' &&
    typeof item.requestId === 'number' &&
    (item.title === null || typeof item.title === 'string') &&
    (item.fromState === null || typeof item.fromState === 'string') &&
    typeof item.toState === 'string' &&
    typeof item.actorName === 'string' &&
    (item.reason === null ||
      item.reason === undefined ||
      typeof item.reason === 'string') &&
    typeof item.occurredAt === 'string'
  )
}

function stateLabel(state: string, t: (key: MessageKey) => string) {
  switch (state) {
    case 'pending':
      return t('history.pending')
    case 'approved':
      return t('history.approved')
    case 'denied':
      return t('history.denied')
    case 'fulfilling':
      return t('history.fulfilling')
    case 'available':
      return t('history.available')
    case 'labeled':
      return t('history.labeled')
    case 'failed':
      return t('history.failed')
    default:
      return state
  }
}

function formatTime(value: string) {
  const parsed = new Date(value)
  if (Number.isNaN(parsed.getTime())) return value
  return parsed.toLocaleString(undefined, {
    dateStyle: 'medium',
    timeStyle: 'short',
  })
}

function ProfileEvents({ profile }: { profile: ProfileRef }) {
  const t = useT()
  const [events, setEvents] = useState<RequestEvent[] | null>(null)
  const [failed, setFailed] = useState(false)

  useEffect(() => {
    let current = true
    request<unknown>('GET', `/api/v1/kid-profiles/${profile.id}/events`)
      .then((body) => {
        if (!current) return
        if (!Array.isArray(body) || !body.every(isEvent)) {
          setFailed(true)
          return
        }
        setEvents(body)
      })
      .catch(() => {
        if (current) setFailed(true)
      })
    return () => {
      current = false
    }
  }, [profile.id])

  return (
    <div className="history-profile">
      <h3>{profile.name}</h3>
      {failed && <p role="alert">{t('history.loadFailed')}</p>}
      {events !== null && events.length === 0 && <p>{t('history.empty')}</p>}
      {events !== null && events.length > 0 && (
        <ul className="history-list" aria-label={profile.name}>
          {events.map((item) => (
            <li key={item.id}>
              <strong>
                {item.title && item.title.trim() !== ''
                  ? item.title
                  : t('history.request', { id: item.requestId })}
              </strong>
              <span>
                {item.fromState === null
                  ? t('history.new')
                  : stateLabel(item.fromState, t)}{' '}
                → {stateLabel(item.toState, t)}
              </span>
              <span>{item.actorName}</span>
              <span className="muted">{formatTime(item.occurredAt)}</span>
              {item.reason && (
                <span>
                  {t('history.reason')}: {item.reason}
                </span>
              )}
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}

export default function RequestHistoryPanel() {
  const t = useT()
  const [profiles, setProfiles] = useState<ProfileRef[] | null>(null)
  const [failed, setFailed] = useState(false)

  useEffect(() => {
    let current = true
    request<unknown>('GET', '/api/v1/kid-profiles')
      .then((body) => {
        if (!current) return
        if (!Array.isArray(body) || !body.every(isProfile)) {
          setFailed(true)
          return
        }
        setProfiles(body)
      })
      .catch(() => {
        if (current) setFailed(true)
      })
    return () => {
      current = false
    }
  }, [])

  return (
    <section
      className="observability"
      aria-labelledby="request-history-heading"
    >
      <h2 id="request-history-heading">{t('history.title')}</h2>
      {failed && <p role="alert">{t('history.loadFailed')}</p>}
      {profiles !== null && profiles.length === 0 && <p>{t('history.none')}</p>}
      {profiles?.map((profile) => (
        <ProfileEvents key={profile.id} profile={profile} />
      ))}
    </section>
  )
}
