import { useEffect, useState } from 'react'
import { request } from '../api'
import { useT } from '../i18n'

type Notice = { id: number; requestId: number; title: string | null }

function isNotice(value: unknown): value is Notice {
  if (!value || typeof value !== 'object') return false
  const body = value as { id?: unknown; requestId?: unknown; title?: unknown }
  return (
    typeof body.id === 'number' &&
    typeof body.requestId === 'number' &&
    (body.title === null || typeof body.title === 'string')
  )
}

export default function ReadyNotice() {
  const t = useT()
  const [notices, setNotices] = useState<Notice[] | null>(null)
  const [failed, setFailed] = useState(false)

  useEffect(() => {
    let current = true
    request<unknown>('GET', '/api/v1/kid/ready-notices').then(
      (body) => {
        if (!current) return
        setNotices(Array.isArray(body) ? body.filter(isNotice) : [])
      },
      () => {
        if (current) setNotices([])
      },
    )
    return () => {
      current = false
    }
  }, [])

  async function dismiss(id: number) {
    setFailed(false)
    try {
      await request('POST', `/api/v1/kid/ready-notices/${id}/dismiss`)
      setNotices(
        (current) => current?.filter((notice) => notice.id !== id) ?? [],
      )
    } catch {
      setFailed(true)
    }
  }

  if (!notices || notices.length === 0) return null

  return (
    <section className="notice" aria-labelledby="ready-notice-title">
      <h2 id="ready-notice-title">{t('readyNotice.heading')}</h2>
      <ul>
        {notices.map((notice) => (
          <li key={notice.id}>
            <p>
              {notice.title
                ? t('readyNotice.item', { title: notice.title })
                : t('readyNotice.generic')}
            </p>
            <button type="button" onClick={() => void dismiss(notice.id)}>
              {t('readyNotice.dismiss')}
            </button>
          </li>
        ))}
      </ul>
      {failed && <p role="alert">{t('readyNotice.failed')}</p>}
    </section>
  )
}
