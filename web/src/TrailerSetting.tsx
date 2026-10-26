import { useEffect, useState } from 'react'
import { request } from './api'
import { useT } from './i18n'

export const trailerSettingKey = 'kids.youtube_trailers'

type SettingRow = { key: string; isSecret: boolean; value: string | null }
type Load = 'loading' | 'loaded' | 'failed'

export default function TrailerSetting() {
  const t = useT()
  const [enabled, setEnabled] = useState(false)
  const [load, setLoad] = useState<Load>('loading')
  const [attempt, setAttempt] = useState(0)
  const [saving, setSaving] = useState(false)
  const [failed, setFailed] = useState(false)

  useEffect(() => {
    let current = true
    request<SettingRow[]>('GET', '/api/v1/settings/').then(
      (rows) => {
        if (!current) return
        const row = rows.find((item) => item.key === trailerSettingKey)
        setEnabled(row?.value === 'true')
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

  if (load === 'loading') {
    return <p role="status">{t('app.loading')}</p>
  }

  if (load === 'failed') {
    return (
      <section className="trailer-setting">
        <h2>{t('parent.trailers')}</h2>
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
      </section>
    )
  }

  return (
    <section className="trailer-setting">
      <h2>{t('parent.trailers')}</h2>
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
      {failed && (
        <p role="alert" className="error">
          {t('parent.trailersFailed')}
        </p>
      )}
    </section>
  )
}
