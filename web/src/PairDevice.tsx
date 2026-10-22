import { useState, type FormEvent } from 'react'
import { useNavigate } from 'react-router-dom'
import { ApiError } from './api'
import { useAuthStatus } from './authStatus'
import { useT } from './i18n'
import { pairThisDevice } from './kidDevices'

export default function PairDevice() {
  const t = useT()
  const navigate = useNavigate()
  const { refresh } = useAuthStatus()
  const [code, setCode] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setError(null)
    setBusy(true)
    try {
      await pairThisDevice(code)
      refresh()
      navigate('/')
    } catch (failure) {
      setError(
        failure instanceof ApiError && failure.status === 400
          ? t('pair.invalid')
          : t('pair.failed'),
      )
    } finally {
      setBusy(false)
    }
  }

  return (
    <form onSubmit={submit} aria-labelledby="pair-heading">
      <h2 id="pair-heading">{t('pair.title')}</h2>
      <p className="muted">{t('pair.intro')}</p>
      <label>
        {t('pair.code')}
        <input
          value={code}
          onChange={(event) => setCode(event.target.value)}
          autoComplete="off"
          autoCapitalize="characters"
          spellCheck={false}
        />
      </label>
      {error && <p role="alert">{error}</p>}
      <button type="submit" disabled={busy || code.trim() === ''}>
        {t('pair.submit')}
      </button>
    </form>
  )
}
