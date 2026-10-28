import { useT } from '../i18n'

export const dogCreditUrl = 'https://www.doesthedogdie.com'

export function DogCredit({ url }: { url: string }) {
  const t = useT()
  if (url !== dogCreditUrl) return null
  return (
    <p>
      <a href={dogCreditUrl} target="_blank" rel="noreferrer noopener">
        {t('queue.warningCredit')}
      </a>
    </p>
  )
}
