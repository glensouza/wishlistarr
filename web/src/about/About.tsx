import { useEffect, useState } from 'react'
import tmdbLogo from '../../../site/tmdb.svg'
import { useT } from '../i18n'
import { readNotices, type NoticePackage } from './notices'
import { WatchProviderCredit } from './WatchProviders'

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

export default function About() {
  const t = useT()
  const [load, setLoad] = useState<Load>('loading')

  useEffect(() => {
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
  }, [])

  return (
    <div className="about-credits">
      <h1>{t('about.title')}</h1>
      <a href="https://www.themoviedb.org/">
        <img className="tmdb-logo" src={tmdbLogo} alt={t('about.tmdbAlt')} />
      </a>
      <p>{t('about.tmdbNotice')}</p>
      <p>
        <WatchProviderCredit />
      </p>
      <p>
        {t('about.fanartBefore')}
        <a href="https://fanart.tv/">{t('about.fanartLink')}</a>
        {t('about.fanartAfter')}
      </p>
      <p>
        {t('about.dogBefore')}
        <a href="https://www.doesthedogdie.com">{t('about.dogLink')}</a>
      </p>
      <p>{t('about.plex')}</p>
      <p>
        {t('about.gplBefore')}
        <a href="https://www.gnu.org/licenses/gpl-3.0.html">
          {t('about.gplLink')}
        </a>
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
