import { useEffect, useState } from 'react'
import KidProfilesAdmin from './KidProfilesAdmin'
import ParentSignIn from './ParentSignIn'

type AuthStatus = { setupRequired: boolean; principalType: string | null }

export default function App() {
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
    <main>
      <h1>Wishlistarr</h1>
      {status?.principalType === 'Parent' && <KidProfilesAdmin />}
      {status !== null && status.principalType !== 'Parent' && (
        <ParentSignIn
          setupRequired={status.setupRequired}
          onSignedIn={() => setVersion((current) => current + 1)}
        />
      )}
    </main>
  )
}
