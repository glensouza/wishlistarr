import { useT } from '../i18n'
import tmdbLogo from '../assets/tmdb.svg'

export function TmdbLogo() {
  const t = useT()
  return <img className="tmdb-logo" src={tmdbLogo} alt={t('about.tmdbAlt')} />
}

export function TmdbNotice() {
  const t = useT()
  return (
    <div className="tmdb-notice">
      <TmdbLogo />
      <p>{t('about.tmdbNotice')}</p>
    </div>
  )
}
