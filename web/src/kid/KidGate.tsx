import type { ReactNode } from 'react'
import { Navigate } from 'react-router-dom'
import { useAuthStatus } from '../authStatus'
import { useT } from '../i18n'

export default function KidGate({ children }: { children: ReactNode }) {
  const t = useT()
  const { loaded, status } = useAuthStatus()

  if (!loaded) {
    return <p role="status">{t('app.loading')}</p>
  }
  if (!status?.kidDevice) {
    return <Navigate to="/parent" replace />
  }
  return children
}
