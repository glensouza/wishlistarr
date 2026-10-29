import { useEffect, useState, type FormEvent } from 'react'
import { useT } from '../i18n'
import {
  avatars,
  createKidProfile,
  listKidProfiles,
  type KidProfile,
} from '../kidProfiles'
import PlexUserPicker from '../PlexUserPicker'
import { setupError } from './setupError'

export default function KidSetupStep({
  profileId,
  onNext,
}: {
  profileId: number | null
  onNext: (id: number) => void
}) {
  const t = useT()
  const [profiles, setProfiles] = useState<KidProfile[]>([])
  const [selected, setSelected] = useState(String(profileId ?? ''))
  const [name, setName] = useState('')
  const [label, setLabel] = useState('')
  const [avatar, setAvatar] = useState(avatars[0])
  const [pin, setPin] = useState('')
  const [plexUser, setPlexUser] = useState('')
  const [movieAge, setMovieAge] = useState('')
  const [tvAge, setTvAge] = useState('')
  const [busy, setBusy] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [attempt, setAttempt] = useState(0)
  const [loaded, setLoaded] = useState(false)

  useEffect(() => {
    let current = true
    listKidProfiles().then(
      (loaded) => {
        if (current) {
          setProfiles(loaded)
          setLoaded(true)
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
  }, [attempt, t])

  async function create(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      const profile = await createKidProfile({
        name,
        avatar,
        pin: pin || undefined,
        plexLabel: label,
        plexUserId: plexUser,
        movieAgeLimit: movieAge === '' ? null : Number(movieAge),
        tvAgeLimit: tvAge === '' ? null : Number(tvAge),
        weeklyAllowance: null,
        autoApprove: false,
        autoApproveMovieRating: null,
        autoApproveTvRating: null,
        blockedGenreIds: [],
        blockedKeywordIds: [],
        allowedOriginalLanguages: [],
        homeRows: ['popular', 'library', 'genre'],
      })
      setPin('')
      onNext(profile.id)
    } catch (failure) {
      setError(setupError(failure, t('setup.saveFailed')))
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      <p>{t('setup.kidHint')}</p>
      {profiles.length > 0 && (
        <>
          <label>
            {t('setup.existingKid')}
            <select
              value={selected}
              disabled={busy}
              onChange={(event) => setSelected(event.target.value)}
            >
              <option value="">{t('setup.newKid')}</option>
              {profiles
                .filter((profile) => profile.plexUserId)
                .map((profile) => (
                  <option key={profile.id} value={profile.id}>
                    {profile.name}
                  </option>
                ))}
            </select>
          </label>
          {selected !== '' && (
            <button
              type="button"
              disabled={
                busy ||
                !profiles.some(
                  (profile) =>
                    String(profile.id) === selected && profile.plexUserId,
                )
              }
              onClick={() => onNext(Number(selected))}
            >
              {t('setup.next')}
            </button>
          )}
        </>
      )}
      {selected === '' && (
        <form onSubmit={(event) => void create(event)}>
          <fieldset disabled={busy || !loaded}>
            <label>
              {t('admin.name')}
              <input
                required
                maxLength={50}
                value={name}
                onChange={(event) => setName(event.target.value)}
              />
            </label>
            <label>
              {t('admin.avatar')}
              <select
                value={avatar}
                onChange={(event) => setAvatar(event.target.value)}
              >
                {avatars.map((value) => (
                  <option key={value} value={value}>
                    {value}
                  </option>
                ))}
              </select>
            </label>
            <label>
              {t('admin.pin')}
              <input
                type="password"
                autoComplete="new-password"
                inputMode="numeric"
                pattern="[0-9]{4,8}"
                value={pin}
                onChange={(event) => setPin(event.target.value)}
              />
            </label>
            <label>
              {t('admin.movieLimit')}
              <input
                type="number"
                min={0}
                max={21}
                step={1}
                value={movieAge}
                onChange={(event) => setMovieAge(event.target.value)}
              />
            </label>
            <label>
              {t('admin.tvLimit')}
              <input
                type="number"
                min={0}
                max={21}
                step={1}
                value={tvAge}
                onChange={(event) => setTvAge(event.target.value)}
              />
            </label>
            <label>
              {t('admin.plexLabel')}
              <input
                maxLength={50}
                value={label}
                onChange={(event) => setLabel(event.target.value)}
              />
            </label>
            <PlexUserPicker value={plexUser} onChange={setPlexUser} />
            <button
              type="submit"
              disabled={name.trim() === '' || plexUser === ''}
            >
              {t('setup.createKid')}
            </button>
          </fieldset>
        </form>
      )}
      {error && (
        <>
          <p role="alert">{error}</p>
          <button
            type="button"
            disabled={busy}
            onClick={() => {
              setError(null)
              setAttempt(attempt + 1)
            }}
          >
            {t('setup.retry')}
          </button>
        </>
      )}
    </>
  )
}
