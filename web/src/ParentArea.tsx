import { useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { resetAntiforgery } from './api'
import { useAuthStatus } from './authStatus'
import { useT } from './i18n'
import { useKidSession } from './kid/KidSession'
import { parentSignOut } from './kidDevices'
import BackupsAdmin from './BackupsAdmin'
import HealthBanner from './HealthBanner'
import KidDevicesAdmin from './KidDevicesAdmin'
import KidProfilesAdmin from './KidProfilesAdmin'
import ContentControls from './ContentControls'
import LogViewer from './LogViewer'
import RequestsAdmin from './RequestsAdmin'
import PairDevice from './PairDevice'
import ParentSignIn from './ParentSignIn'
import TrailerSetting from './TrailerSetting'
import UpdateNotice from './UpdateNotice'
import ParentHome from './ParentHome'
import RequestHistoryPanel from './RequestHistoryPanel'
import NotificationSettings from './NotificationSettings'

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
          <UpdateNotice />
          <HealthBanner />
          <p>
            <Link to="/about">{t('about.link')}</Link>
          </p>
          <ParentHome />
          <RequestsAdmin />
          <KidProfilesAdmin />
          <ContentControls />
          <KidDevicesAdmin />
          <TrailerSetting />
          <NotificationSettings />
          <BackupsAdmin />
          <RequestHistoryPanel />
          <LogViewer />
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
