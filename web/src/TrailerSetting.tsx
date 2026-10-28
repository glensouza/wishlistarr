import { useEffect, useState } from 'react'
import { request } from './api'
import { useT } from './i18n'

export const trailerSettingKey = 'kids.youtube_trailers'
export const trailerSourcesKey = 'kids.trailer_sources'

type SettingRow = { key: string; isSecret: boolean; value: string | null }
type Load = 'loading' | 'loaded' | 'failed'

export default function TrailerSetting() {
  const t = useT()
  const [enabled, setEnabled] = useState(false)
  const [load, setLoad] = useState<Load>('loading')
  const [attempt, setAttempt] = useState(0)
  const [saving, setSaving] = useState(false)
  const [failed, setFailed] = useState(false)
  const [sources, setSources] = useState('youtube')

  useEffect(() => {
    let current = true
    request<SettingRow[]>('GET', '/api/v1/settings/').then(
      (rows) => {
        if (!current) return
        const row = rows.find((item) => item.key === trailerSettingKey)
        setEnabled(row?.value === 'true')
        const sourceRow = rows.find((item) => item.key === trailerSourcesKey)
        setSources(sourceRow?.value ?? 'youtube')
        setLoad('loaded')
      },
      () => {
        if (current) setLoad('failed')
      },
    )
    return () => {
      current = false
    }
  }, [attempt])

  async function change(next: boolean) {
    if (saving || load !== 'loaded') return
    const previous = enabled
    setSaving(true)
    setFailed(false)
    setEnabled(next)
    try {
      await request('PUT', `/api/v1/settings/${trailerSettingKey}`, {
        value: next ? 'true' : 'false',
        isSecret: false,
      })
    } catch {
      setEnabled(previous)
      setFailed(true)
    } finally {
      setSaving(false)
    }
  }

  async function changeSources(next: string) {
    if (saving || load !== 'loaded') return
    const previous = sources
    setSaving(true)
    setFailed(false)
    setSources(next)
    try {
      await request('PUT', `/api/v1/settings/${trailerSourcesKey}`, {
        value: next,
        isSecret: false,
      })
    } catch {
      setSources(previous)
      setFailed(true)
    } finally {
      setSaving(false)
    }
  }

  return (
    <section className="trailer-setting">
      <h2 id="trailers-heading">{t('parent.trailers')}</h2>
      {load === 'loading' && <p role="status">{t('app.loading')}</p>}
      {load === 'failed' && (
        <>
          <p role="alert" className="error">
            {t('parent.trailersLoadFailed')}
          </p>
          <button
            type="button"
            onClick={() => {
              setLoad('loading')
              setAttempt((count) => count + 1)
            }}
          >
            {t('parent.trailersRetry')}
          </button>
        </>
      )}
      {load === 'loaded' && (
        <>
          <label className="trailer-toggle">
            <input
              type="checkbox"
              checked={enabled}
              disabled={saving}
              onChange={(event) => void change(event.target.checked)}
            />
            {t('parent.trailersLabel')}
          </label>
          <p className="muted">{t('parent.trailersHint')}</p>
          <label>
            {t('parent.trailerSources')}
            <select
              value={sources}
              disabled={saving}
              onChange={(event) => void changeSources(event.target.value)}
            >
              <option value="none">{t('parent.trailerNone')}</option>
              <option value="plex,youtube">
                {t('parent.trailerPlexFirst')}
              </option>
              <option value="youtube,plex">
                {t('parent.trailerYoutubeFirst')}
              </option>
              <option value="plex">{t('parent.trailerPlexOnly')}</option>
              <option value="youtube">{t('parent.trailerYoutubeOnly')}</option>
            </select>
          </label>
          {failed && (
            <p role="alert" className="error">
              {t('parent.trailersFailed')}
            </p>
          )}
        </>
      )}
    </section>
  )
}
