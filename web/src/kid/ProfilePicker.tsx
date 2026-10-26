import { useEffect, useRef, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { ApiError } from '../api'
import { useT } from '../i18n'
import Avatar from './Avatar'
import { kidSignIn, listPickerProfiles, type PickerProfile } from './kidApi'
import { useKidSession } from './KidSession'
import PinPad from './PinPad'
import { useKidSpatialNav } from './useKidSpatialNav'

export default function ProfilePicker() {
  const t = useT()
  const navigate = useNavigate()
  const { signedIn } = useKidSession()
  const [profiles, setProfiles] = useState<PickerProfile[] | null>(null)
  const [loadFailed, setLoadFailed] = useState(false)
  const [version, setVersion] = useState(0)
  const [chosen, setChosen] = useState<PickerProfile | null>(null)
  const rootRef = useRef<HTMLDivElement>(null)
  useKidSpatialNav(rootRef)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    let current = true
    listPickerProfiles().then(
      (loaded) => {
        if (!current) return
        setProfiles(loaded)
        setLoadFailed(false)
      },
      () => current && setLoadFailed(true),
    )
    return () => {
      current = false
    }
  }, [version])

  function describe(failure: unknown) {
    if (failure instanceof ApiError) {
      if (failure.status === 401) return t('pin.invalid')
      if (failure.status === 423) return t('pin.locked')
      if (failure.status === 429) {
        return failure.retryAfter === null
          ? t('pin.wait')
          : t('pin.waitSeconds', { seconds: failure.retryAfter })
      }
    }
    return t('pin.failed')
  }

  async function signIn(profile: PickerProfile, pin?: string) {
    setBusy(true)
    setError(null)
    try {
      await kidSignIn(profile.id, pin)
      signedIn(profile.name)
      navigate('/home')
    } catch (failure) {
      setError(describe(failure))
    } finally {
      setBusy(false)
    }
  }

  function choose(profile: PickerProfile) {
    setError(null)
    if (profile.requiresPin) {
      setChosen(profile)
    } else {
      void signIn(profile)
    }
  }

  return (
    <div ref={rootRef}>
      {chosen ? (
        <PinPad
          name={chosen.name}
          error={error}
          busy={busy}
          onSubmit={(pin) => void signIn(chosen, pin)}
          onCancel={() => {
            setChosen(null)
            setError(null)
          }}
        />
      ) : (
        <section aria-labelledby="picker-title" className="picker">
          <h1 id="picker-title">{t('picker.title')}</h1>
          <p className="muted">{t('picker.subtitle')}</p>
          {error && (
            <p className="error" role="alert">
              {error}
            </p>
          )}
          {loadFailed && (
            <div role="alert">
              <p className="error">{t('picker.loadFailed')}</p>
              <button type="button" onClick={() => setVersion((v) => v + 1)}>
                {t('picker.retry')}
              </button>
            </div>
          )}
          {profiles === null && !loadFailed && (
            <p role="status">{t('app.loading')}</p>
          )}
          {profiles?.length === 0 && <p>{t('picker.empty')}</p>}
          <ul className="profile-grid">
            {profiles?.map((profile) => (
              <li key={profile.id}>
                <button
                  type="button"
                  className="profile-card"
                  disabled={busy}
                  aria-label={t('picker.profileLabel', { name: profile.name })}
                  onClick={() => choose(profile)}
                >
                  <Avatar avatar={profile.avatar} name={profile.name} />
                  <span className="profile-name">{profile.name}</span>
                </button>
              </li>
            ))}
          </ul>
        </section>
      )}
    </div>
  )
}
