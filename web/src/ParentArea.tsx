import { useEffect, useState } from 'react'
import { useT } from './i18n'
import KidProfilesAdmin from './KidProfilesAdmin'
import ParentSignIn from './ParentSignIn'

type AuthStatus = { setupRequired: boolean; principalType: string | null }

export default function ParentArea() {
  const t = useT()
  const [status, setStatus] = useState<AuthStatus | null>(null)
  const [version, setVersion] = useState(0)

  useEffect(() => {
    let current = true
    fetch('/api/v1/auth/status')
      .then((response) => response.json() as Promise<AuthStatus>)
      .then(
        (loaded) => current && setStatus(loaded),
        () => current && setStatus(null),
      )
    return () => {
      current = false
    }
  }, [version])

  return (
    <>
      <h1>{t('parent.title')}</h1>
      {status?.principalType === 'Parent' && <KidProfilesAdmin />}
      {status !== null && status.principalType !== 'Parent' && (
        <ParentSignIn
          setupRequired={status.setupRequired}
          onSignedIn={() => setVersion((current) => current + 1)}
        />
      )}
    </>
  )
}
