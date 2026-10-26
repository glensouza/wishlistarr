import { useEffect, useState, type ReactNode } from 'react'
import { Navigate } from 'react-router-dom'
import { useAuthStatus } from '../authStatus'
import { useT } from '../i18n'
import { readNotices, type NoticePackage } from './notices'
import { TmdbLogo } from './TmdbNotice'

type Load = 'loading' | 'unavailable' | NoticePackage[]

async function loadFile(path: string): Promise<NoticePackage[] | null> {
  const response = await fetch(path)
  if (!response.ok) return null
  const body = await response.text()
  if (body.trimStart().startsWith('<')) return null
  return readNotices(JSON.parse(body) as unknown)
}

function compare(left: NoticePackage, right: NoticePackage): number {
  if (left.name < right.name) return -1
  if (left.name > right.name) return 1
  if (left.version < right.version) return -1
  if (left.version > right.version) return 1
  return 0
}

function ExternalLink({
  href,
  children,
}: {
  href: string
  children: ReactNode
}) {
  return (
    <a href={href} target="_blank" rel="noreferrer noopener">
      {children}
    </a>
  )
}

export default function About() {
  const t = useT()
  const { loaded, status } = useAuthStatus()
  const isParent = status?.principalType === 'Parent'
  const [load, setLoad] = useState<Load>('loading')

  useEffect(() => {
    if (!isParent) return
    let current = true
    Promise.all([
      loadFile('/licenses/npm.json'),
      loadFile('/licenses/nuget.json'),
    ])
      .then(([npm, nuget]) => {
        if (!current) return
        if (!npm || !nuget) {
          setLoad('unavailable')
          return
        }
        setLoad([...npm, ...nuget].sort(compare))
      })
      .catch(() => {
        if (current) setLoad('unavailable')
      })
    return () => {
      current = false
    }
  }, [isParent])

  if (!loaded) return <p role="status">{t('app.loading')}</p>
  if (!isParent) return <Navigate to="/parent" replace />

  return (
    <div className="about-credits">
      <h1>{t('about.title')}</h1>
      <ExternalLink href="https://www.themoviedb.org/">
        <TmdbLogo />
      </ExternalLink>
      <p>{t('about.tmdbNotice')}</p>
      <p>
        {t('providers.creditBefore')}
        <ExternalLink href="https://www.justwatch.com/">
          {t('providers.justWatch')}
        </ExternalLink>
      </p>
      <p>
        {t('about.fanartBefore')}
        <ExternalLink href="https://fanart.tv/">
          {t('about.fanartLink')}
        </ExternalLink>
        {t('about.fanartAfter')}
      </p>
      <p>
        {t('about.dogBefore')}
        <ExternalLink href="https://www.doesthedogdie.com">
          {t('about.dogLink')}
        </ExternalLink>
      </p>
      <p>{t('about.plex')}</p>
      <p>
        {t('about.gplBefore')}
        <ExternalLink href="https://www.gnu.org/licenses/gpl-3.0.html">
          {t('about.gplLink')}
        </ExternalLink>
        {t('about.gplAfter')}
      </p>
      <h2>{t('about.licenses')}</h2>
      {load === 'loading' && <p role="status">{t('about.licensesLoading')}</p>}
      {load === 'unavailable' && (
        <p role="status">{t('about.licensesUnavailable')}</p>
      )}
      {Array.isArray(load) && (
        <ul className="license-list">
          {load.map((pkg, index) => (
            <li key={`${pkg.name}@${pkg.version}@${index}`}>
              {t('about.package', pkg)}
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
