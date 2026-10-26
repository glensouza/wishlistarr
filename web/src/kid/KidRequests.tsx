import { useEffect, useState } from 'react'
import { useT, type MessageKey } from '../i18n'
import { listKidRequests, type KidRequest } from './kidApi'

const stateKey = (state: string): MessageKey => {
  switch (state) {
    case 'pending':
      return 'kidRequests.pending'
    case 'approved':
      return 'kidRequests.approved'
    case 'denied':
      return 'kidRequests.denied'
    case 'fulfilling':
      return 'kidRequests.fulfilling'
    case 'available':
      return 'kidRequests.available'
    case 'labeled':
      return 'kidRequests.labeled'
    case 'failed':
      return 'kidRequests.failed'
    default:
      return 'kidRequests.unknown'
  }
}

export default function KidRequests() {
  const t = useT()
  const [rows, setRows] = useState<KidRequest[] | null>(null)
  const [error, setError] = useState(false)

  useEffect(() => {
    let current = true
    listKidRequests().then(
      (loaded) => current && setRows(loaded),
      () => current && setError(true),
    )
    return () => {
      current = false
    }
  }, [])

  return (
    <section aria-labelledby="kid-requests-heading">
      <h2 id="kid-requests-heading">{t('kidRequests.title')}</h2>
      {error && <p role="alert">{t('app.somethingWrong')}</p>}
      {rows === null && !error && <p role="status">{t('app.loading')}</p>}
      {rows !== null && rows.length === 0 && <p>{t('kidRequests.none')}</p>}
      {rows !== null && rows.length > 0 && (
        <ul>
          {rows.map((row) => (
            <li key={row.id}>
              <strong>{row.title}</strong>
              <span>{t(stateKey(row.state))}</span>
              {row.state === 'denied' && row.denialReason && (
                <p>{row.denialReason}</p>
              )}
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}
