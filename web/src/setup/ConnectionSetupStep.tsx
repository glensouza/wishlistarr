import { useEffect, useState } from 'react'
import { request } from '../api'
import { TmdbNotice } from '../about/TmdbNotice'
import { useT } from '../i18n'
import { setupError } from './setupError'

type Service = 'tmdb' | 'seerr' | 'sonarr' | 'radarr'
type Saved = {
  url: string | null
  hasApiKey: boolean
  userId?: number
  qualityProfileId?: number
  rootFolderPath?: string
  tagIds?: number[]
}
type Profile = { id: number; name: string }
type Folder = { path: string }
type TestResult = { valid?: boolean; message?: string; warnings?: string[] }

export default function ConnectionSetupStep({
  service,
  onNext,
  onValidated,
}: {
  service: Service
  onNext?: () => void
  onValidated?: (valid: boolean) => void
}) {
  const t = useT()
  const arr = service === 'sonarr' || service === 'radarr'
  const [url, setUrl] = useState('')
  const [key, setKey] = useState('')
  const [saved, setSaved] = useState(false)
  const [userId, setUserId] = useState('1')
  const [profile, setProfile] = useState('')
  const [folder, setFolder] = useState('')
  const [tags, setTags] = useState<number[]>([])
  const [profiles, setProfiles] = useState<Profile[]>([])
  const [folders, setFolders] = useState<Folder[]>([])
  const [busy, setBusy] = useState(true)
  const [valid, setValid] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [message, setMessage] = useState('')
  const [warnings, setWarnings] = useState<string[]>([])
  const [failed, setFailed] = useState<string | null>(null)

  useEffect(() => {
    let current = true
    async function load() {
      try {
        if (service === 'tmdb') {
          const settings = await request<{ key: string; isSecret: boolean }[]>(
            'GET',
            '/api/v1/settings',
          )
          if (current)
            setSaved(
              settings.some(
                (entry) => entry.key === 'tmdb.api_key' && entry.isSecret,
              ),
            )
        } else {
          const settings = await request<Saved>(
            'GET',
            `/api/v1/settings/${service}`,
          )
          if (!current) return
          setUrl(settings.url ?? '')
          setSaved(settings.hasApiKey)
          setUserId(String(settings.userId ?? 1))
          setProfile(String(settings.qualityProfileId ?? ''))
          setFolder(settings.rootFolderPath ?? '')
          setTags(settings.tagIds ?? [])
        }
      } catch (failure) {
        if (current) setError(setupError(failure, t('setup.loadFailed')))
      } finally {
        if (current) setBusy(false)
      }
    }
    void load()
    return () => {
      current = false
    }
  }, [service, t])

  function invalidate() {
    setValid(false)
    setMessage('')
    setWarnings([])
    onValidated?.(false)
  }

  function candidate() {
    const apiKey = key.trim() || undefined
    if (service === 'tmdb') return { apiKey }
    if (service === 'seerr') return { url, apiKey, userId: Number(userId) }
    return { url, apiKey }
  }

  async function testCandidate(): Promise<string | null> {
    try {
      const result = await request<TestResult>(
        'POST',
        `/api/v1/settings/${service}/test-candidate`,
        candidate(),
      )
      if (service !== 'seerr' && result.valid !== true)
        return result.message ?? t('setup.testFailed')
      setWarnings(result.warnings ?? [])
      return null
    } catch (failure) {
      return setupError(failure, t('setup.testFailed'))
    }
  }

  async function store() {
    if (service === 'tmdb') {
      await request('PUT', '/api/v1/settings/tmdb.api_key', {
        value: key.trim(),
        isSecret: true,
      })
      setKey('')
      setSaved(true)
      return
    }
    const settings = await request<Saved>(
      'PUT',
      `/api/v1/settings/${service}`,
      {
        url,
        apiKey: key.trim() || undefined,
        ...(arr
          ? {
              qualityProfileId: profile === '' ? null : Number(profile),
              rootFolderPath: folder || null,
              tagIds: tags,
            }
          : { userId: Number(userId) }),
      },
    )
    setKey('')
    setSaved(settings.hasApiKey)
    setUrl(settings.url ?? '')
  }

  async function save() {
    setBusy(true)
    setError(null)
    invalidate()
    const attempt = JSON.stringify(candidate())
    const savedKeyOnly = service === 'tmdb' && key.trim() === ''
    const saveAnyway = failed === attempt
    try {
      if (savedKeyOnly) {
        const result = await request<TestResult>(
          'POST',
          '/api/v1/settings/tmdb/test',
        )
        if (result.valid !== true) {
          setError(result.message ?? t('setup.testFailed'))
          return
        }
      } else if (!saveAnyway) {
        const problem = await testCandidate()
        if (problem !== null) {
          setFailed(attempt)
          setError(problem)
          return
        }
      }
      setFailed(null)
      if (!savedKeyOnly) await store()
      if (saveAnyway) {
        setMessage(t('setup.savedUntested'))
        return
      }
      if (arr) {
        const qualities = await request<Profile[]>(
          'GET',
          `/api/v1/${service}/profiles`,
        )
        const roots = await request<Folder[]>(
          'GET',
          `/api/v1/${service}/rootfolders`,
        )
        setProfiles(qualities)
        setFolders(roots)
        if (
          !qualities.some((entry) => String(entry.id) === profile) ||
          !roots.some((entry) => entry.path === folder)
        ) {
          setError(t('setup.chooseOptions'))
          return
        }
      }
      setValid(true)
      onValidated?.(true)
      setMessage(t('setup.testPassed'))
    } catch (failure) {
      setError(setupError(failure, t('setup.testFailed')))
    } finally {
      setBusy(false)
    }
  }

  return (
    <section aria-label={t(`setup.${service}`)}>
      {service === 'tmdb' && (
        <>
          <p>{t('setup.tmdbHint')}</p>
          <TmdbNotice />
        </>
      )}
      {service !== 'tmdb' && <h4>{t(`setup.${service}`)}</h4>}
      {service === 'seerr' && <p>{t('setup.seerrHint')}</p>}
      {arr && <p>{t('setup.arrHint')}</p>}
      <fieldset disabled={busy}>
        {service !== 'tmdb' && (
          <label>
            {t('setup.address')}
            <input
              type="url"
              value={url}
              maxLength={500}
              onChange={(event) => {
                setUrl(event.target.value)
                invalidate()
                setProfiles([])
                setFolders([])
              }}
            />
          </label>
        )}
        <label>
          {t('setup.apiKey')}
          <input
            type="password"
            autoComplete="new-password"
            value={key}
            onChange={(event) => {
              setKey(event.target.value)
              invalidate()
            }}
          />
        </label>
        {saved && <p>{t('setup.secretSaved')}</p>}
        {service === 'seerr' && (
          <label>
            {t('setup.seerrUser')}
            <input
              type="number"
              min={1}
              step={1}
              value={userId}
              onChange={(event) => {
                setUserId(event.target.value)
                invalidate()
              }}
            />
          </label>
        )}
        {arr && (
          <>
            <label>
              {t('setup.quality')}
              <select
                value={profile}
                onChange={(event) => {
                  setProfile(event.target.value)
                  invalidate()
                }}
              >
                <option value="">{t('setup.choose')}</option>
                {profile !== '' &&
                  !profiles.some((entry) => String(entry.id) === profile) && (
                    <option value={profile}>{profile}</option>
                  )}
                {profiles.map((entry) => (
                  <option key={entry.id} value={entry.id}>
                    {entry.name}
                  </option>
                ))}
              </select>
            </label>
            <label>
              {t('setup.rootFolder')}
              <select
                value={folder}
                onChange={(event) => {
                  setFolder(event.target.value)
                  invalidate()
                }}
              >
                <option value="">{t('setup.choose')}</option>
                {folder !== '' &&
                  !folders.some((entry) => entry.path === folder) && (
                    <option value={folder}>{folder}</option>
                  )}
                {folders.map((entry) => (
                  <option key={entry.path} value={entry.path}>
                    {entry.path}
                  </option>
                ))}
              </select>
            </label>
          </>
        )}
        <button
          type="button"
          disabled={
            (!saved && key.trim() === '') ||
            (service !== 'tmdb' && url.trim() === '')
          }
          onClick={() => void save()}
        >
          {t('setup.saveTest')}
        </button>
      </fieldset>
      {busy && <p role="status">{t('setup.checking')}</p>}
      {message && <p role="status">{message}</p>}
      {warnings.length > 0 && (
        <ul aria-label={t('setup.warnings')}>
          {warnings.map((warning) => (
            <li key={warning}>{warning}</li>
          ))}
        </ul>
      )}
      {error && <p role="alert">{error}</p>}
      {error && failed === JSON.stringify(candidate()) && (
        <p>{t('setup.saveAnyway')}</p>
      )}
      {onNext && (
        <button type="button" disabled={!valid || busy} onClick={onNext}>
          {t('setup.next')}
        </button>
      )}
    </section>
  )
}
