import { useEffect, useState } from 'react'
import { useT, type MessageKey } from '../i18n'
import {
  listKidHistory,
  listKidRequests,
  type KidHistoryAction,
  type KidHistoryEntry,
  type KidRequest,
} from './kidApi'

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
    case 'revoked':
      return 'kidRequests.revoked'
    default:
      return 'kidRequests.unknown'
  }
}

const historyKey = (action: KidHistoryAction): MessageKey => {
  switch (action) {
    case 'granted':
      return 'kidRequests.granted'
    case 'asked':
      return 'kidRequests.asked'
    case 'approved':
      return 'kidRequests.historyApproved'
    case 'autoApproved':
      return 'kidRequests.historyAutoApproved'
    case 'denied':
      return 'kidRequests.historyDenied'
    case 'ready':
      return 'kidRequests.ready'
    case 'revoked':
      return 'kidRequests.historyRevoked'
    case 'failed':
      return 'kidRequests.historyFailed'
  }
}

export default function KidRequests() {
  const t = useT()
  const [rows, setRows] = useState<KidRequest[] | null>(null)
  const [history, setHistory] = useState<KidHistoryEntry[] | null>(null)
  const [rowsFailed, setRowsFailed] = useState(false)
  const [historyFailed, setHistoryFailed] = useState(false)
  const [version, setVersion] = useState(0)

  useEffect(() => {
    let current = true
    void Promise.allSettled([listKidRequests(), listKidHistory()]).then(
      ([loaded, events]) => {
        if (!current) return
        if (loaded.status === 'fulfilled') setRows(loaded.value)
        setRowsFailed(loaded.status === 'rejected')
        if (events.status === 'fulfilled') setHistory(events.value)
        setHistoryFailed(events.status === 'rejected')
      },
    )
    return () => {
      current = false
    }
  }, [version])

  const retry = (
    <button type="button" onClick={() => setVersion((value) => value + 1)}>
      {t('kidRequests.retry')}
    </button>
  )

  return (
    <section aria-labelledby="kid-requests-heading">
      <h2 id="kid-requests-heading">{t('kidRequests.title')}</h2>
      {rowsFailed && (
        <div role="alert">
          <p>{t('kidRequests.listFailedToLoad')}</p>
          {retry}
        </div>
      )}
      {rows === null && !rowsFailed && <p role="status">{t('app.loading')}</p>}
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
      <h3>{t('kidRequests.history')}</h3>
      {historyFailed && (
        <div role="alert">
          <p>{t('kidRequests.historyFailedToLoad')}</p>
          {retry}
        </div>
      )}
      {history !== null && history.length === 0 && (
        <p>{t('kidRequests.historyEmpty')}</p>
      )}
      {history !== null && history.length > 0 && (
        <ul>
          {history.map((entry) => (
            <li key={entry.id}>
              <strong>{entry.title}</strong>
              <span>{t(historyKey(entry.action))}</span>
              {entry.action === 'denied' && entry.note && <p>{entry.note}</p>}
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}
