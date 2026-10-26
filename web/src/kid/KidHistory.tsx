import { useEffect, useState } from 'react'
import { useT, type MessageKey } from '../i18n'
import {
  historyPageSize,
  listKidHistory,
  type KidHistoryAction,
  type KidHistoryEntry,
} from './kidApi'

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

export default function KidHistory() {
  const t = useT()
  const [history, setHistory] = useState<KidHistoryEntry[] | null>(null)
  const [historyFailed, setHistoryFailed] = useState(false)
  const [moreHistory, setMoreHistory] = useState(false)
  const [loadingMore, setLoadingMore] = useState(false)
  const [moreFailed, setMoreFailed] = useState(false)
  const [version, setVersion] = useState(0)

  useEffect(() => {
    let current = true
    void listKidHistory().then(
      (events) => {
        if (!current) return
        setHistory(events)
        setMoreHistory(events.length === historyPageSize)
        setHistoryFailed(false)
        setMoreFailed(false)
      },
      () => {
        if (!current) return
        setHistoryFailed(true)
        setMoreFailed(false)
      },
    )
    return () => {
      current = false
    }
  }, [version])

  async function showMore() {
    if (loadingMore || history === null) return
    setLoadingMore(true)
    setMoreFailed(false)
    try {
      const page = await listKidHistory(history.length)
      setHistory((current) => [
        ...(current ?? []),
        ...page.filter(
          (entry) => !(current ?? []).some((item) => item.id === entry.id),
        ),
      ])
      setMoreHistory(page.length === historyPageSize)
    } catch {
      setMoreFailed(true)
    } finally {
      setLoadingMore(false)
    }
  }

  const retry = (
    <button type="button" onClick={() => setVersion((value) => value + 1)}>
      {t('kidRequests.retry')}
    </button>
  )

  return (
    <section aria-labelledby="kid-history-heading">
      <h2 id="kid-history-heading">{t('kidRequests.history')}</h2>
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
      {moreFailed && <p role="alert">{t('kidRequests.historyFailedToLoad')}</p>}
      {history !== null && moreHistory && (
        <button
          type="button"
          disabled={loadingMore}
          onClick={() => void showMore()}
        >
          {t('kidRequests.showMore')}
        </button>
      )}
    </section>
  )
}
