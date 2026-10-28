import { useEffect, useState } from 'react'
import { request } from './api'
import { useT } from './i18n'

type SettingRow = { key: string; isSecret: boolean; value: string | null }
const key = 'fanart.api_key'
const clientKey = 'fanart.client_key'

export default function FanartSetting() {
  const t = useT()
  const [configured, setConfigured] = useState(false)
  const [value, setValue] = useState('')
  const [clientConfigured, setClientConfigured] = useState(false)
  const [clientValue, setClientValue] = useState('')
  const [status, setStatus] = useState<'loading' | 'ready' | 'failed'>(
    'loading',
  )
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState(false)
  const [attempt, setAttempt] = useState(0)
  useEffect(() => {
    let active = true
    request<SettingRow[]>('GET', '/api/v1/settings/').then(
      (rows) => {
        if (active) {
          setConfigured(rows.some((row) => row.key === key && row.isSecret))
          setClientConfigured(
            rows.some((row) => row.key === clientKey && row.isSecret),
          )
          setStatus('ready')
        }
      },
      () => {
        if (active) setStatus('failed')
      },
    )
    return () => {
      active = false
    }
  }, [attempt])
  async function save(next: string, personal = false) {
    setSaving(true)
    setError(false)
    try {
      await request('PUT', `/api/v1/settings/${personal ? clientKey : key}`, {
        value: next,
        isSecret: true,
      })
      if (personal) {
        setClientConfigured(next.trim().length > 0)
        setClientValue('')
      } else {
        setConfigured(next.trim().length > 0)
        setValue('')
      }
    } catch {
      setError(true)
    } finally {
      setSaving(false)
    }
  }
  return (
    <section>
      <h2>{t('parent.fanartHeading')}</h2>
      <p>{t('parent.fanartHint')}</p>
      {status === 'loading' && <p role="status">{t('app.loading')}</p>}
      {status === 'failed' && (
        <>
          <p role="alert">{t('parent.fanartLoadFailed')}</p>
          <button
            type="button"
            onClick={() => {
              setStatus('loading')
              setAttempt((count) => count + 1)
            }}
          >
            {t('parent.trailersRetry')}
          </button>
        </>
      )}
      {status === 'ready' && (
        <>
          <p>
            {configured
              ? t('parent.fanartConfigured')
              : t('parent.fanartNotConfigured')}
          </p>
          <label>
            {t('parent.fanartKey')}
            <input
              type="password"
              autoComplete="off"
              value={value}
              onChange={(event) => setValue(event.target.value)}
            />
          </label>
          <button
            type="button"
            disabled={saving || !value.trim()}
            onClick={() => void save(value.trim())}
          >
            {t('parent.fanartSave')}
          </button>
          {configured && (
            <button
              type="button"
              disabled={saving}
              onClick={() => void save('')}
            >
              {t('parent.fanartRemove')}
            </button>
          )}
          <p>
            {clientConfigured
              ? t('parent.fanartPersonalConfigured')
              : t('parent.fanartPersonalNotConfigured')}
          </p>
          <label>
            {t('parent.fanartPersonalKey')}
            <input
              type="password"
              autoComplete="off"
              value={clientValue}
              onChange={(event) => setClientValue(event.target.value)}
            />
          </label>
          <button
            type="button"
            disabled={saving || !clientValue.trim()}
            onClick={() => void save(clientValue.trim(), true)}
          >
            {t('parent.fanartPersonalSave')}
          </button>
          {clientConfigured && (
            <button
              type="button"
              disabled={saving}
              onClick={() => void save('', true)}
            >
              {t('parent.fanartPersonalRemove')}
            </button>
          )}
          {error && <p role="alert">{t('parent.fanartSaveFailed')}</p>}
        </>
      )}
    </section>
  )
}
