import { Link, Outlet } from 'react-router-dom'
import { TmdbNotice } from './about/TmdbNotice'
import { useT } from './i18n'
import ThemeToggle from './theme/ThemeToggle'

export default function Shell() {
  const t = useT()
  return (
    <div className="shell">
      <a className="skip-link" href="#main">
        {t('app.skipToContent')}
      </a>
      <header className="shell-header">
        <Link to="/" className="brand">
          {t('app.name')}
        </Link>
        <nav aria-label={t('nav.parents')} className="shell-nav">
          <Link to="/parent">{t('nav.parents')}</Link>
          <ThemeToggle />
        </nav>
      </header>
      <main id="main" tabIndex={-1} className="shell-main">
        <Outlet />
      </main>
      <footer className="shell-footer">
        <TmdbNotice />
      </footer>
    </div>
  )
}
