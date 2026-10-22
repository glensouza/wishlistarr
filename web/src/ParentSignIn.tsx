import { useState, type FormEvent } from 'react'
import { useT } from './i18n'
import { request, resetAntiforgery } from './api'
import { exitKidMode } from './kidDevices'

type Props = {
  setupRequired: boolean
  onSignedIn: () => void
  kidDeviceName?: string
}

export default function ParentSignIn({
  setupRequired,
  onSignedIn,
  kidDeviceName,
}: Props) {
  const t = useT()
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const exiting = kidDeviceName !== undefined

  async function submit(event: FormEvent) {
    event.preventDefault()
    setError(null)
    try {
      if (exiting) {
        await exitKidMode(username, password)
      } else {
        await request(
          'POST',
          setupRequired ? '/api/v1/auth/setup' : '/api/v1/auth/login',
          { username, password },
        )
      }
      resetAntiforgery()
      onSignedIn()
    } catch (failure) {
      setError(
        failure instanceof Error ? failure.message : t('parent.signInFailed'),
      )
    }
  }

  let title = setupRequired ? t('parent.setupTitle') : t('parent.signInTitle')
  let action = setupRequired ? t('parent.createAccount') : t('parent.signIn')
  if (exiting) {
    title = t('exit.title')
    action = t('exit.submit')
  }

  return (
    <form onSubmit={submit}>
      <h2>{title}</h2>
      {exiting && (
        <p className="muted">{t('exit.intro', { device: kidDeviceName })}</p>
      )}
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
      <button type="submit">{action}</button>
    </form>
  )
}
