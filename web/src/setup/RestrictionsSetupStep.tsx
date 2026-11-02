import { useEffect, useState } from 'react'
import { request } from '../api'
import { useT } from '../i18n'
import { listKidProfiles, type KidProfile } from '../kidProfiles'
import { setupError } from './setupError'

export default function RestrictionsSetupStep({
  profileId,
  onComplete,
}: {
  profileId: number
  onComplete: () => void
}) {
  const t = useT()
  const [profile, setProfile] = useState<KidProfile | null>(null)
  const [busy, setBusy] = useState(true)
  const [valid, setValid] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let current = true
    listKidProfiles().then(
      (kids) => {
        if (current) {
          setProfile(kids.find((kid) => kid.id === profileId) ?? null)
          setBusy(false)
        }
      },
      () => {
        if (current) {
          setError(t('setup.profilesFailed'))
          setBusy(false)
        }
      },
    )
    return () => {
      current = false
    }
  }, [profileId, t])

  async function check(complete: boolean) {
    setBusy(true)
    setValid(false)
    setError(null)
    try {
      const kid = (await listKidProfiles()).find((kid) => kid.id === profileId)
      setProfile(kid ?? null)
      if (
        !kid ||
        kid.plexRestrictions?.movies.restricted !== true ||
        kid.plexRestrictions.television.restricted !== true
      ) {
        setError(t('setup.restrictionsFailed'))
        return
      }
      setValid(true)
      if (complete) {
        await request('POST', '/api/v1/setup/complete', { profileId })
        onComplete()
      }
    } catch (failure) {
      setError(setupError(failure, t('setup.finishFailed')))
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      <p>{t('setup.restrictionsHint')}</p>
      <p>{t('setup.restrictionsWait')}</p>
      {profile && (
        <>
          <p>
            {t('setup.profileLabel', {
              name: profile.name,
              label: profile.plexLabel,
            })}
          </p>
          <ul aria-label={t('admin.restriction.heading')}>
            {(['movies', 'television'] as const).map((kind) => (
              <li key={kind}>
                {t(
                  profile.plexRestrictions?.[kind].restricted === true
                    ? 'setup.restrictionConfirmed'
                    : 'setup.restrictionUnconfirmed',
                  { type: t(`admin.restriction.${kind}`) },
                )}
              </li>
            ))}
          </ul>
        </>
      )}
      <button type="button" disabled={busy} onClick={() => void check(false)}>
        {t('setup.checkRestrictions')}
      </button>
      {busy && <p role="status">{t('setup.checking')}</p>}
      {valid && !error && <p role="status">{t('setup.restrictionsPassed')}</p>}
      {error && <p role="alert">{error}</p>}
      <button
        type="button"
        disabled={!valid || busy}
        onClick={() => void check(true)}
      >
        {t('setup.finish')}
      </button>
    </>
  )
}
