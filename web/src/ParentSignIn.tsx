import { useState, type FormEvent } from 'react'
import { request, resetAntiforgery } from './api'

type Props = { setupRequired: boolean; onSignedIn: () => void }

export default function ParentSignIn({ setupRequired, onSignedIn }: Props) {
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
      setError(failure instanceof Error ? failure.message : 'Sign-in failed')
    }
  }

  return (
    <form onSubmit={submit}>
      <h2>{setupRequired ? 'Create the parent account' : 'Parent sign-in'}</h2>
      <label>
        Username
        <input
          value={username}
          onChange={(event) => setUsername(event.target.value)}
          autoComplete="username"
        />
      </label>
      <label>
        Password
        <input
          type="password"
          value={password}
          onChange={(event) => setPassword(event.target.value)}
          autoComplete={setupRequired ? 'new-password' : 'current-password'}
        />
      </label>
      {error && <p role="alert">{error}</p>}
      <button type="submit">
        {setupRequired ? 'Create account' : 'Sign in'}
      </button>
    </form>
  )
}
