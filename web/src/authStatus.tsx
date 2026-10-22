import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from 'react'

export type AuthStatus = {
  setupRequired: boolean
  principalType: string | null
  kidDevice: { name: string } | null
}

type AuthStatusValue = {
  loaded: boolean
  status: AuthStatus | null
  refresh: () => void
}

const AuthStatusContext = createContext<AuthStatusValue>({
  loaded: false,
  status: null,
  refresh: () => {},
})

export function AuthStatusProvider({ children }: { children: ReactNode }) {
  const [state, setState] = useState<{
    loaded: boolean
    status: AuthStatus | null
  }>({ loaded: false, status: null })
  const [version, setVersion] = useState(0)

  useEffect(() => {
    let current = true
    fetch('/api/v1/auth/status')
      .then((response) => {
        if (!response.ok) throw new Error(String(response.status))
        return response.json() as Promise<AuthStatus>
      })
      .then(
        (loaded) =>
          current &&
          setState({
            loaded: true,
            status: { ...loaded, kidDevice: loaded.kidDevice ?? null },
          }),
        () => current && setState({ loaded: true, status: null }),
      )
    return () => {
      current = false
    }
  }, [version])

  const refresh = useCallback(() => setVersion((value) => value + 1), [])
  const value = useMemo(() => ({ ...state, refresh }), [state, refresh])
  return (
    <AuthStatusContext.Provider value={value}>
      {children}
    </AuthStatusContext.Provider>
  )
}

export const useAuthStatus = () => useContext(AuthStatusContext)
