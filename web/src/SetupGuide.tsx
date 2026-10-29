import { useEffect, useRef, useState, type ReactNode } from 'react'
import { request } from './api'
import { useT, type MessageKey } from './i18n'
import PlexSetupStep from './setup/PlexSetupStep'
import ConnectionSetupStep from './setup/ConnectionSetupStep'
import KidSetupStep from './setup/KidSetupStep'
import RestrictionsSetupStep from './setup/RestrictionsSetupStep'
import { setupError } from './setup/setupError'

const steps: MessageKey[] = [
  'setup.plex',
  'setup.tmdb',
  'setup.requests',
  'setup.kid',
  'setup.restrictions',
]

export default function SetupGuide({ children }: { children: ReactNode }) {
  const t = useT()
  const [state, setState] = useState<'loading' | 'open' | 'closed'>('loading')
  const [loadFailed, setLoadFailed] = useState(false)
  const [attempt, setAttempt] = useState(0)
  const [step, setStep] = useState(0)
  const [profileId, setProfileId] = useState<number | null>(null)
  const heading = useRef<HTMLHeadingElement>(null)

  useEffect(() => {
    let current = true
    request<{ completed: boolean; showGuide: boolean }>(
      'GET',
      '/api/v1/setup',
    ).then(
      (status) => {
        if (!current) return
        setLoadFailed(false)
        setState((now) =>
          status.showGuide ? 'open' : now === 'loading' ? 'closed' : now,
        )
      },
      () => {
        if (!current) return
        setLoadFailed(true)
        setState((now) => (now === 'loading' ? 'closed' : now))
      },
    )
    return () => {
      current = false
    }
  }, [attempt])

  useEffect(() => {
    if (state === 'open') heading.current?.focus()
  }, [step, state])

  function exit() {
    setState('closed')
    request('POST', '/api/v1/setup/dismiss').catch(() => undefined)
  }

  if (state === 'loading') return <p role="status">{t('setup.loading')}</p>
  if (state === 'closed')
    return (
      <>
        <section aria-labelledby="setup-settings-heading">
          <h2 id="setup-settings-heading">{t('setup.settings')}</h2>
          {loadFailed && (
            <p role="alert">
              {t('setup.loadFailed')}{' '}
              <button
                type="button"
                onClick={() => {
                  setLoadFailed(false)
                  setAttempt(attempt + 1)
                }}
              >
                {t('setup.retry')}
              </button>
            </p>
          )}
          <button
            type="button"
            onClick={() => {
              setStep(0)
              setProfileId(null)
              setState('open')
            }}
          >
            {t('setup.rerun')}
          </button>
        </section>
        {children}
      </>
    )

  const next = () =>
    setStep((current) => (current === step ? step + 1 : current))
  return (
    <section className="setup-guide" aria-labelledby="setup-heading">
      <h2 id="setup-heading" tabIndex={-1} ref={heading}>
        {t('setup.title')}
      </h2>
      <ol aria-label={t('setup.steps')}>
        {steps.map((key, index) => (
          <li key={key} aria-current={index === step ? 'step' : undefined}>
            {t(key)}
          </li>
        ))}
      </ol>
      <h3>{t(steps[step])}</h3>
      <div key={step}>
        {step === 0 && <PlexSetupStep onNext={next} />}
        {step === 1 && <ConnectionSetupStep service="tmdb" onNext={next} />}
        {step === 2 && <RequestServiceStep onNext={next} />}
        {step === 3 && (
          <KidSetupStep
            profileId={profileId}
            onNext={(id) => {
              setProfileId(id)
              next()
            }}
          />
        )}
        {step === 4 && profileId !== null && (
          <RestrictionsSetupStep
            profileId={profileId}
            onComplete={() => setState('closed')}
          />
        )}
      </div>
      {step > 0 && (
        <button type="button" onClick={() => setStep(step - 1)}>
          {t('setup.back')}
        </button>
      )}
      <button type="button" onClick={exit}>
        {t('setup.exit')}
      </button>
    </section>
  )
}

function RequestServiceStep({ onNext }: { onNext: () => void }) {
  const t = useT()
  const [mode, setMode] = useState<'seerr' | 'arr'>('seerr')
  const [ready, setReady] = useState<string[]>([])
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  async function saveMode() {
    setBusy(true)
    setError(null)
    try {
      await request('PUT', '/api/v1/settings/fulfilment.mode', { value: mode })
      onNext()
    } catch (failure) {
      setError(setupError(failure, t('setup.saveFailed')))
    } finally {
      setBusy(false)
    }
  }
  function tested(service: string, valid: boolean) {
    setReady((current) =>
      valid
        ? [...new Set([...current, service])]
        : current.filter((item) => item !== service),
    )
  }
  const valid =
    mode === 'seerr'
      ? ready.includes('seerr')
      : ready.includes('sonarr') && ready.includes('radarr')
  return (
    <>
      <p>{t('setup.requestHint')}</p>
      <label>
        {t('setup.requestMode')}
        <select
          value={mode}
          disabled={busy}
          onChange={(event) => {
            setMode(event.target.value as 'seerr' | 'arr')
            setReady([])
          }}
        >
          <option value="seerr">{t('setup.seerr')}</option>
          <option value="arr">{t('setup.arr')}</option>
        </select>
      </label>
      {mode === 'seerr' ? (
        <ConnectionSetupStep
          key="seerr"
          service="seerr"
          onValidated={(valid) => tested('seerr', valid)}
        />
      ) : (
        <>
          <ConnectionSetupStep
            service="sonarr"
            onValidated={(valid) => tested('sonarr', valid)}
          />
          <ConnectionSetupStep
            service="radarr"
            onValidated={(valid) => tested('radarr', valid)}
          />
        </>
      )}
      {error && <p role="alert">{error}</p>}
      <button
        type="button"
        disabled={!valid || busy}
        onClick={() => void saveMode()}
      >
        {t('setup.next')}
      </button>
    </>
  )
}
