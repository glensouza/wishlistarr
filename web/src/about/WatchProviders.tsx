import { useT } from '../i18n'

export type WatchProvider = { name: string }

export function WatchProviderCredit() {
  const t = useT()
  return (
    <>
      {t('providers.creditBefore')}
      {t('providers.justWatch')}
    </>
  )
}

export function WatchProviders({
  providers,
}: {
  providers: readonly WatchProvider[]
}) {
  const t = useT()
  const names = providers
    .map((provider) => provider.name.trim())
    .filter((name) => name.length > 0)
  if (names.length === 0) return null
  return (
    <section className="watch-providers" aria-label={t('providers.list')}>
      <ul>
        {names.map((name, index) => (
          <li key={`${name}-${index}`}>{name}</li>
        ))}
      </ul>
      <p>
        <WatchProviderCredit />
      </p>
    </section>
  )
}
