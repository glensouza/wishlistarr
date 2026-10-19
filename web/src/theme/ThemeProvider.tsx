import {
  createContext,
  useContext,
  useEffect,
  useLayoutEffect,
  useMemo,
  useState,
  type ReactNode,
} from 'react'

export type ThemePreference = 'system' | 'light' | 'dark'

const storageKey = 'wishlistarr.theme'
const query = '(prefers-color-scheme: dark)'

type ThemeValue = {
  preference: ThemePreference
  resolved: 'light' | 'dark'
  setPreference: (preference: ThemePreference) => void
}

const ThemeContext = createContext<ThemeValue>({
  preference: 'system',
  resolved: 'light',
  setPreference: () => {},
})

function readPreference(): ThemePreference {
  try {
    const stored = localStorage.getItem(storageKey)
    return stored === 'light' || stored === 'dark' ? stored : 'system'
  } catch {
    return 'system'
  }
}

const systemPrefersDark = () =>
  typeof window.matchMedia === 'function' && window.matchMedia(query).matches

export function ThemeProvider({ children }: { children: ReactNode }) {
  const [preference, setPreference] = useState(readPreference)
  const [systemDark, setSystemDark] = useState(systemPrefersDark)

  useEffect(() => {
    if (typeof window.matchMedia !== 'function') return
    const media = window.matchMedia(query)
    const listener = (event: MediaQueryListEvent) =>
      setSystemDark(event.matches)
    media.addEventListener('change', listener)
    return () => media.removeEventListener('change', listener)
  }, [])

  const resolved =
    preference === 'system' ? (systemDark ? 'dark' : 'light') : preference

  useLayoutEffect(() => {
    document.documentElement.dataset.theme = resolved
  }, [resolved])

  const value = useMemo<ThemeValue>(
    () => ({
      preference,
      resolved,
      setPreference: (next) => {
        setPreference(next)
        try {
          if (next === 'system') localStorage.removeItem(storageKey)
          else localStorage.setItem(storageKey, next)
        } catch {
          // Storage can be unavailable; the choice then lasts for this visit only.
        }
      },
    }),
    [preference, resolved],
  )

  return <ThemeContext.Provider value={value}>{children}</ThemeContext.Provider>
}

export const useTheme = () => useContext(ThemeContext)
