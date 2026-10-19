import { useState, type FormEvent } from 'react'
import { useT } from './i18n'
import { request, resetAntiforgery } from './api'

type Props = { setupRequired: boolean; onSignedIn: () => void }

export default function ParentSignIn({ setupRequired, onSignedIn }: Props) {
  const t = useT()
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setError(null)
    try {
      await request(
        'POST',
        setupRequired ? '/api/v1/auth/setup' : '/api/v1/auth/login',
        { username, password },
      )
      resetAntiforgery()
      onSignedIn()
    } catch (failure) {
      setError(
        failure instanceof Error ? failure.message : t('parent.signInFailed'),
      )
    }
  }

  return (
    <form onSubmit={submit}>
      <h2>
        {setupRequired ? t('parent.setupTitle') : t('parent.signInTitle')}
      </h2>
      <label>
        {t('parent.username')}
        <input
          value={username}
          onChange={(event) => setUsername(event.target.value)}
          autoComplete="username"
        />
      </label>
      <label>
        {t('parent.password')}
        <input
          type="password"
          value={password}
          onChange={(event) => setPassword(event.target.value)}
          autoComplete={setupRequired ? 'new-password' : 'current-password'}
        />
      </label>
      {error && <p role="alert">{error}</p>}
      <button type="submit">
        {setupRequired ? t('parent.createAccount') : t('parent.signIn')}
      </button>
    </form>
  )
}
