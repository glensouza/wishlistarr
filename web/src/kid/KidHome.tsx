import { useRef } from 'react'
import { Navigate, useNavigate } from 'react-router-dom'
import { useT } from '../i18n'
import { useKidSession } from './KidSession'
import HomeRows from './HomeRows'
import KidHistory from './KidHistory'
import Search from './Search'
import { useKidSpatialNav } from './useKidSpatialNav'
import Wishlist from './Wishlist'

export default function KidHome() {
  const t = useT()
  const navigate = useNavigate()
  const { session, signOut } = useKidSession()
  const rootRef = useRef<HTMLElement>(null)
  useKidSpatialNav(rootRef)

  if (session.status === 'loading') {
    return <p role="status">{t('app.loading')}</p>
  }
  if (session.status === 'out') {
    return <Navigate to="/" replace />
  }

  return (
    <section ref={rootRef} aria-labelledby="home-title">
      <h1 id="home-title">{t('home.welcome', { name: session.name })}</h1>
      <p>{t('home.intro')}</p>
      <HomeRows />
      <Wishlist />
      <KidHistory />
      <Search />
      <button
        type="button"
        onClick={() => void signOut().then(() => navigate('/'))}
      >
        {t('home.signOut')}
      </button>
    </section>
  )
}
