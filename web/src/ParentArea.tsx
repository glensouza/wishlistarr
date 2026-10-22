import { useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { resetAntiforgery } from './api'
import { useAuthStatus } from './authStatus'
import { useT } from './i18n'
import { useKidSession } from './kid/KidSession'
import { parentSignOut } from './kidDevices'
import KidDevicesAdmin from './KidDevicesAdmin'
import KidProfilesAdmin from './KidProfilesAdmin'
import PairDevice from './PairDevice'
import ParentSignIn from './ParentSignIn'

export default function ParentArea() {
  const t = useT()
  const navigate = useNavigate()
  const { status, refresh } = useAuthStatus()
  const { clear } = useKidSession()
  const [error, setError] = useState<string | null>(null)

  async function signOut() {
    setError(null)
    try {
      await parentSignOut()
      resetAntiforgery()
      refresh()
      if (status?.kidDevice) navigate('/')
    } catch (failure) {
      setError(
        failure instanceof Error ? failure.message : t('app.somethingWrong'),
      )
    }
  }

  const isParent = status?.principalType === 'Parent'
  const kidDevice = status?.kidDevice ?? null

  return (
    <>
      <h1>{t('parent.title')}</h1>
      {isParent && (
        <>
          <KidProfilesAdmin />
          <KidDevicesAdmin />
          {error && <p role="alert">{error}</p>}
          <button type="button" onClick={() => void signOut()}>
            {kidDevice ? t('parent.returnToKidMode') : t('parent.signOut')}
          </button>
        </>
      )}
      {status !== null && !isParent && kidDevice && (
        <>
          <ParentSignIn
            setupRequired={false}
            kidDeviceName={kidDevice.name}
            onSignedIn={() => {
              clear()
              refresh()
            }}
          />
          <Link to="/">{t('exit.back')}</Link>
        </>
      )}
      {status !== null && !isParent && !kidDevice && (
        <>
          <ParentSignIn
            setupRequired={status.setupRequired}
            onSignedIn={refresh}
          />
          {!status.setupRequired && <PairDevice />}
        </>
      )}
    </>
  )
}
