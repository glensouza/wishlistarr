import { Navigate, useNavigate } from 'react-router-dom'
import { useT } from '../i18n'
import { useKidSession } from './KidSession'
import HomeRows from './HomeRows'
import KidRequests from './KidRequests'
import Search from './Search'
import Wishlist from './Wishlist'

export default function KidHome() {
  const t = useT()
  const navigate = useNavigate()
  const { session, signOut } = useKidSession()

  if (session.status === 'loading') {
    return <p role="status">{t('app.loading')}</p>
  }
  if (session.status === 'out') {
    return <Navigate to="/" replace />
  }

  return (
    <section aria-labelledby="home-title">
      <h1 id="home-title">{t('home.welcome', { name: session.name })}</h1>
      <p>{t('home.intro')}</p>
      <HomeRows />
      <Wishlist />
      <KidRequests />
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
