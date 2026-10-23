import { useEffect, useState } from 'react'
import { request } from './api'
import { useT } from './i18n'

export const trailerSettingKey = 'kids.youtube_trailers'

type SettingRow = { key: string; isSecret: boolean; value: string | null }

export default function TrailerSetting() {
  const t = useT()
  const [enabled, setEnabled] = useState(false)
  const [loaded, setLoaded] = useState(false)
  const [saving, setSaving] = useState(false)
  const [failed, setFailed] = useState(false)

  useEffect(() => {
    let current = true
    request<SettingRow[]>('GET', '/api/v1/settings/').then(
      (rows) => {
        if (!current) return
        const row = rows.find((item) => item.key === trailerSettingKey)
        setEnabled(row?.value === 'true')
        setLoaded(true)
      },
      () => {
        if (!current) return
        setEnabled(false)
        setLoaded(true)
      },
    )
    return () => {
      current = false
    }
  }, [])

  async function change(next: boolean) {
    if (saving) return
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

  if (!loaded) {
    return <p role="status">{t('app.loading')}</p>
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
