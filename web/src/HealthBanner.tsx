import { useEffect, useState } from 'react'
import { request } from './api'
import { useT, type MessageKey } from './i18n'

type HealthStatus = 'ok' | 'warning' | 'error'

type HealthCheck = {
  integration: string
  status: HealthStatus
  message: string
  fix: string
}

function isCheck(value: unknown): value is HealthCheck {
  if (typeof value !== 'object' || value === null) return false
  const check = value as Record<string, unknown>
  return (
    typeof check.integration === 'string' &&
    (check.status === 'ok' ||
      check.status === 'warning' ||
      check.status === 'error') &&
    typeof check.message === 'string' &&
    typeof check.fix === 'string'
  )
}

function integrationLabel(
  name: string,
  t: (key: MessageKey) => string,
): string {
  switch (name) {
    case 'plex':
      return t('health.plex')
    case 'tmdb':
      return t('health.tmdb')
    case 'seerr':
      return t('health.seerr')
    case 'sonarr':
      return t('health.sonarr')
    case 'radarr':
      return t('health.radarr')
    case 'webhooks':
      return t('health.webhooks')
    case 'jobs':
      return t('health.jobs')
    case 'database':
      return t('health.database')
    default:
      return name
  }
}

function statusLabel(status: HealthStatus, t: (key: MessageKey) => string) {
  switch (status) {
    case 'ok':
      return t('health.ok')
    case 'warning':
      return t('health.warning')
    case 'error':
      return t('health.error')
  }
}

export default function HealthBanner() {
  const t = useT()
  const [checks, setChecks] = useState<HealthCheck[] | null>(null)
  const [failed, setFailed] = useState(false)

  useEffect(() => {
    let current = true
    request<unknown>('GET', '/api/v1/system/health')
      .then((body) => {
        if (!current) return
        if (!Array.isArray(body) || !body.every(isCheck)) {
          setFailed(true)
          return
        }
        setChecks(body)
      })
      .catch(() => {
        if (current) setFailed(true)
      })
    return () => {
      current = false
    }
  }, [])

  const failing = (checks ?? []).filter((check) => check.status !== 'ok')
  const severe = failing.some((check) => check.status === 'error')

  return (
    <section className="observability" aria-labelledby="health-heading">
      <h2 id="health-heading">{t('health.title')}</h2>
      {failed && <p role="alert">{t('health.loadFailed')}</p>}
      {checks !== null && failing.length > 0 && (
        <div
          role="alert"
          className={
            severe ? 'health-banner health-banner-error' : 'health-banner'
          }
        >
          <ul>
            {failing.map((check) => (
              <li key={check.integration}>
                <strong>{integrationLabel(check.integration, t)}</strong>
                <span>{check.message}</span>
                <span>
                  {t('health.fix')}: {check.fix}
                </span>
              </li>
            ))}
          </ul>
        </div>
      )}
      {checks !== null && checks.length > 0 && failing.length === 0 && (
        <p>{t('health.passed')}</p>
      )}
      {checks !== null && checks.length > 0 && (
        <ul className="check-list">
          {checks.map((check) => (
            <li key={check.integration}>
              <strong>{integrationLabel(check.integration, t)}</strong>
              <span className="muted">{statusLabel(check.status, t)}</span>
              <span>{check.message}</span>
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}
