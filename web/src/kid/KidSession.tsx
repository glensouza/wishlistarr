import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from 'react'
import { resetAntiforgery } from '../api'
import { getKidMe, kidSignOut } from './kidApi'

type SessionState =
  { status: 'loading' } | { status: 'out' } | { status: 'in'; name: string }

type SessionValue = {
  session: SessionState
  signedIn: (name: string) => void
  signOut: () => Promise<void>
  clear: () => void
}

const KidSessionContext = createContext<SessionValue>({
  session: { status: 'out' },
  signedIn: () => {},
  signOut: async () => {},
  clear: () => {},
})

export function KidSessionProvider({ children }: { children: ReactNode }) {
  const [session, setSession] = useState<SessionState>({ status: 'loading' })

  useEffect(() => {
    let current = true
    getKidMe().then(
      (me) => current && setSession({ status: 'in', name: me.name }),
      () => current && setSession({ status: 'out' }),
    )
    return () => {
      current = false
    }
  }, [])

  const signedIn = useCallback((name: string) => {
    resetAntiforgery()
    setSession({ status: 'in', name })
  }, [])

  const signOut = useCallback(async () => {
    try {
      await kidSignOut()
    } finally {
      resetAntiforgery()
      setSession({ status: 'out' })
    }
  }, [])

  const clear = useCallback(() => setSession({ status: 'out' }), [])

  const value = useMemo(
    () => ({ session, signedIn, signOut, clear }),
    [session, signedIn, signOut, clear],
  )
  return (
    <KidSessionContext.Provider value={value}>
      {children}
    </KidSessionContext.Provider>
  )
}

export const useKidSession = () => useContext(KidSessionContext)
