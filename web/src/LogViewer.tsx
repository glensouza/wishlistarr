import { useEffect, useState } from 'react'
import { request } from './api'
import { useT, type MessageKey } from './i18n'

type LogEntry = {
  at: string
  level: string
  category: string
  message: string
}

const levels = [
  'all',
  'trace',
  'debug',
  'information',
  'warning',
  'error',
  'critical',
] as const

type LogLevel = (typeof levels)[number]

function isEntry(value: unknown): value is LogEntry {
  if (typeof value !== 'object' || value === null) return false
  const entry = value as Record<string, unknown>
  return (
    typeof entry.at === 'string' &&
    typeof entry.level === 'string' &&
    typeof entry.category === 'string' &&
    typeof entry.message === 'string'
  )
}

function logsPath(level: LogLevel, download = false) {
  const base = download ? '/api/v1/system/logs/download' : '/api/v1/system/logs'
  return level === 'all' ? base : `${base}?level=${level}`
}

function levelLabel(level: string, t: (key: MessageKey) => string) {
  switch (level) {
    case 'all':
      return t('logs.all')
    case 'trace':
      return t('logs.trace')
    case 'debug':
      return t('logs.debug')
    case 'information':
      return t('logs.information')
    case 'warning':
      return t('logs.warning')
    case 'error':
      return t('logs.error')
    case 'critical':
      return t('logs.critical')
    default:
      return level
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

export default function LogViewer() {
  const t = useT()
  const [level, setLevel] = useState<LogLevel>('information')
  const [entries, setEntries] = useState<LogEntry[] | null>(null)
  const [failed, setFailed] = useState(false)

  useEffect(() => {
    let current = true
    request<unknown>('GET', logsPath(level))
      .then((body) => {
        if (!current) return
        if (!Array.isArray(body) || !body.every(isEntry)) {
          setFailed(true)
          setEntries(null)
          return
        }
        setFailed(false)
        setEntries(body)
      })
      .catch(() => {
        if (!current) return
        setFailed(true)
        setEntries(null)
      })
    return () => {
      current = false
    }
  }, [level])

  return (
    <section className="observability" aria-labelledby="logs-heading">
      <h2 id="logs-heading">{t('logs.title')}</h2>
      <div className="log-toolbar">
        <label>
          {t('logs.level')}
          <select
            value={level}
            onChange={(event) => setLevel(event.target.value as LogLevel)}
          >
            {levels.map((option) => (
              <option key={option} value={option}>
                {levelLabel(option, t)}
              </option>
            ))}
          </select>
        </label>
        <a href={logsPath(level, true)}>{t('logs.download')}</a>
      </div>
      {failed && <p role="alert">{t('logs.loadFailed')}</p>}
      {entries !== null && entries.length === 0 && <p>{t('logs.empty')}</p>}
      {entries !== null && entries.length > 0 && (
        <ul className="log-list">
          {entries.map((entry) => (
            <li key={`${entry.at}-${entry.level}-${entry.message}`}>
              <span className="muted">{formatTime(entry.at)}</span>
              <strong>{levelLabel(entry.level, t)}</strong>
              <span className="muted">{entry.category}</span>
              <span>{entry.message}</span>
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}
