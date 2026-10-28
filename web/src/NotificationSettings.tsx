import { useEffect, useState, type FormEvent } from 'react'
import { ApiError, request } from './api'
import { useT, type MessageKey } from './i18n'
import WebPushSettings from './WebPushSettings'

const providers = ['ntfy', 'discord', 'apprise', 'smtp'] as const
const events = [
  'pending',
  'approved',
  'denied',
  'available',
  'failed',
  'pinFailures',
  'profileLocked',
] as const
type Provider = (typeof providers)[number]
type Configuration = Record<string, string | number | boolean>
export type NotificationProvider = {
  provider: Provider
  enabled: boolean
  events: string[]
  configuration: Configuration
  hasSecret: boolean
}
export type NotificationConfiguration = {
  publicBaseUrl: string
  providers: NotificationProvider[]
}

const fields: Record<Provider, string[]> = {
  ntfy: ['url', 'token'],
  discord: ['url'],
  apprise: ['url', 'targets'],
  smtp: ['host', 'port', 'username', 'password', 'from', 'to'],
}
// Only 400 and 502 answers carry a reason written by this server; upstream bodies never reach them.
function reason(failure: unknown) {
  return failure instanceof ApiError &&
    (failure.status === 400 || failure.status === 502) &&
    !failure.message.startsWith('Request failed')
    ? failure.message
    : null
}

function Feedback({
  message,
  failed,
  detail,
}: {
  message: MessageKey | null
  failed: boolean
  detail: string | null
}) {
  const t = useT()
  return (
    <>
      <p role="status">{message && !failed ? t(message) : ''}</p>
      {message && failed && (
        <p role="alert">
          {t(message)}
          {detail && ` ${detail}`}
        </p>
      )}
    </>
  )
}

function isSecret(field: string) {
  return (
    field === 'url' ||
    field === 'token' ||
    field === 'password' ||
    field === 'targets'
  )
}

export default function NotificationSettings() {
  const t = useT()
  const [open, setOpen] = useState(false)
  return (
    <section aria-labelledby="notifications-heading">
      <h2 id="notifications-heading">{t('notifications.title')}</h2>
      <button type="button" aria-expanded={open} onClick={() => setOpen(!open)}>
        {t(open ? 'notifications.close' : 'notifications.open')}
      </button>
      {open && <Settings />}
    </section>
  )
}

function Settings() {
  const t = useT()
  const [configuration, setConfiguration] =
    useState<NotificationConfiguration | null>(null)
  const [failed, setFailed] = useState(false)
  const [attempt, setAttempt] = useState(0)
  const [active, setActive] = useState<Provider>('ntfy')
  useEffect(() => {
    let current = true
    request<NotificationConfiguration>('GET', '/api/v1/notifications').then(
      (value) => {
        if (current) setConfiguration(value)
      },
      () => {
        if (current) setFailed(true)
      },
    )
    return () => {
      current = false
    }
  }, [attempt])
  if (failed)
    return (
      <>
        <p role="alert">{t('notifications.loadFailed')}</p>
        <button
          type="button"
          onClick={() => {
            setFailed(false)
            setAttempt(attempt + 1)
          }}
        >
          {t('notifications.retry')}
        </button>
      </>
    )
  if (!configuration) return <p role="status">{t('app.loading')}</p>
  return (
    <>
      <WebPushSettings />
      <BaseUrl initial={configuration.publicBaseUrl} />
      <div role="group" aria-label={t('notifications.providers')}>
        {providers.map((provider) => (
          <button
            type="button"
            key={provider}
            aria-pressed={active === provider}
            aria-controls={`notifications-${provider}`}
            onClick={() => setActive(provider)}
          >
            {t(`notifications.provider.${provider}`)}
          </button>
        ))}
      </div>
      {providers.map((provider) => (
        <div
          key={provider}
          id={`notifications-${provider}`}
          hidden={active !== provider}
        >
          <ProviderForm
            initial={
              configuration.providers.find(
                (row) => row.provider === provider,
              ) ?? {
                provider,
                enabled: false,
                events: [...events],
                configuration: {},
                hasSecret: false,
              }
            }
          />
        </div>
      ))}
    </>
  )
}

function BaseUrl({ initial }: { initial: string }) {
  const t = useT()
  const [url, setUrl] = useState(initial)
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState<MessageKey | null>(null)
  const [failed, setFailed] = useState(false)
  const [detail, setDetail] = useState<string | null>(null)
  async function save(event: FormEvent) {
    event.preventDefault()
    try {
      const parsed = new URL(url)
      if (
        !['http:', 'https:'].includes(parsed.protocol) ||
        parsed.username ||
        parsed.password ||
        parsed.pathname !== '/' ||
        parsed.search ||
        parsed.hash ||
        url.trim().length > 256
      )
        throw new Error()
    } catch {
      setFailed(true)
      setDetail(null)
      setMessage('notifications.invalidBaseUrl')
      return
    }
    setBusy(true)
    setMessage(null)
    try {
      await request('PUT', '/api/v1/notifications/base-url', {
        url: url.trim(),
      })
      setFailed(false)
      setMessage('notifications.saved')
    } catch (failure) {
      setFailed(true)
      setDetail(reason(failure))
      setMessage('notifications.saveFailed')
    } finally {
      setBusy(false)
    }
  }
  return (
    <form onSubmit={(event) => void save(event)}>
      <fieldset disabled={busy}>
        <legend>{t('notifications.baseUrlTitle')}</legend>
        <label>
          {t('notifications.baseUrl')}
          <input
            type="url"
            required
            maxLength={256}
            value={url}
            onChange={(event) => {
              setUrl(event.target.value)
              setMessage(null)
            }}
          />
        </label>
        <p className="muted">{t('notifications.baseUrlHint')}</p>
        <button type="submit">{t('notifications.saveBaseUrl')}</button>
        <Feedback message={message} failed={failed} detail={detail} />
      </fieldset>
    </form>
  )
}

function ProviderForm({ initial }: { initial: NotificationProvider }) {
  const t = useT()
  const provider = initial.provider
  const [enabled, setEnabled] = useState(initial.enabled)
  const [selected, setSelected] = useState(initial.events)
  const [configuration, setConfiguration] = useState<Configuration>(
    provider === 'smtp'
      ? { port: 587, enableSsl: true, ...initial.configuration }
      : initial.configuration,
  )
  const [hasSecret, setHasSecret] = useState(initial.hasSecret)
  const [dirty, setDirty] = useState(false)
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState<MessageKey | null>(null)
  const [failed, setFailed] = useState(false)
  const [detail, setDetail] = useState<string | null>(null)
  function changed() {
    setDirty(true)
    setMessage(null)
  }
  async function run(test: boolean) {
    setBusy(true)
    setMessage(null)
    try {
      const values = Object.fromEntries(
        Object.entries(configuration).filter(
          ([key, value]) => value !== '' || (!isSecret(key) && key !== 'port'),
        ),
      )
      if (test) await request('POST', `/api/v1/notifications/${provider}/test`)
      else {
        await request('PUT', `/api/v1/notifications/${provider}`, {
          enabled,
          events: selected,
          configuration: values,
        })
        setHasSecret(
          hasSecret ||
            Object.entries(values).some(
              ([key, value]) => isSecret(key) && Boolean(value),
            ),
        )
        setConfiguration(
          Object.fromEntries(
            Object.entries(configuration).map(([key, value]) => [
              key,
              isSecret(key) ? '' : value,
            ]),
          ),
        )
        setDirty(false)
      }
      setFailed(false)
      setMessage(test ? 'notifications.testSent' : 'notifications.saved')
    } catch (failure) {
      setFailed(true)
      setDetail(reason(failure))
      setMessage(test ? 'notifications.testFailed' : 'notifications.saveFailed')
    } finally {
      setBusy(false)
    }
  }
  return (
    <form
      onSubmit={(event) => {
        event.preventDefault()
        void run(false)
      }}
    >
      <fieldset disabled={busy}>
        <legend>{t(`notifications.provider.${provider}`)}</legend>
        <label>
          <input
            type="checkbox"
            checked={enabled}
            onChange={(event) => {
              setEnabled(event.target.checked)
              changed()
            }}
          />
          {t('notifications.enabled')}
        </label>
        <fieldset>
          <legend>{t('notifications.events')}</legend>
          {events.map((event) => (
            <label key={event}>
              <input
                type="checkbox"
                checked={selected.includes(event)}
                onChange={(change) => {
                  setSelected(
                    change.target.checked
                      ? [...selected, event]
                      : selected.filter((value) => value !== event),
                  )
                  changed()
                }}
              />
              {t(`notifications.event.${event}`)}
            </label>
          ))}
        </fieldset>
        <p className="muted">
          {t(
            hasSecret
              ? 'notifications.secretConfigured'
              : 'notifications.secretMissing',
          )}
        </p>
        {provider === 'apprise' && (
          <p className="muted" id="notifications-apprise-endpoint-hint">
            {t('notifications.appriseHint')}
          </p>
        )}
        {fields[provider].map((field) => {
          const secret = isSecret(field)
          return (
            <label key={field}>
              {t(`notifications.field.${field}` as MessageKey)}
              <input
                type={
                  secret
                    ? 'password'
                    : field === 'port'
                      ? 'number'
                      : field === 'url'
                        ? 'url'
                        : 'text'
                }
                autoComplete={secret ? 'new-password' : 'off'}
                min={field === 'port' ? 1 : undefined}
                max={field === 'port' ? 65535 : undefined}
                value={String(
                  configuration[field] ?? (field === 'port' ? 587 : ''),
                )}
                aria-describedby={
                  secret
                    ? `notifications-${provider}-secret-hint${provider === 'apprise' ? ' notifications-apprise-endpoint-hint' : ''}`
                    : undefined
                }
                onChange={(event) => {
                  setConfiguration({
                    ...configuration,
                    [field]:
                      field === 'port' && event.target.value !== ''
                        ? Number(event.target.value)
                        : event.target.value,
                  })
                  changed()
                }}
              />
            </label>
          )
        })}
        <p className="muted" id={`notifications-${provider}-secret-hint`}>
          {t('notifications.secretHint')}
        </p>
        {provider === 'smtp' && (
          <label>
            <input
              type="checkbox"
              checked={configuration.enableSsl === true}
              onChange={(event) => {
                setConfiguration({
                  ...configuration,
                  enableSsl: event.target.checked,
                })
                changed()
              }}
            />
            {t('notifications.field.enableSsl')}
          </label>
        )}
        <button type="submit">{t('notifications.save')}</button>
        <button type="button" disabled={dirty} onClick={() => void run(true)}>
          {t('notifications.test')}
        </button>
        {dirty && <p className="muted">{t('notifications.testHint')}</p>}
        <Feedback message={message} failed={failed} detail={detail} />
      </fieldset>
    </form>
  )
}
