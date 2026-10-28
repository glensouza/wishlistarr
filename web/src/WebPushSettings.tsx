import { useEffect, useState } from 'react'
import { request } from './api'
import { useT } from './i18n'
import { PushUnavailable, subscribeBrowser } from './webPush'

type SavedBrowser = { id: number; createdAt: string }
type ParentPush = { publicKey: string; subscriptions: SavedBrowser[] }

function isParentPush(value: unknown): value is ParentPush {
  if (!value || typeof value !== 'object') return false
  const body = value as { publicKey?: unknown; subscriptions?: unknown }
  return typeof body.publicKey === 'string' && Array.isArray(body.subscriptions)
}

export default function WebPushSettings() {
  const t = useT()
  const [loaded, setLoaded] = useState<ParentPush | null>(null)
  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState(false)
  const [removed, setRemoved] = useState(false)
  const [problem, setProblem] = useState<
    'unsupported' | 'denied' | 'failed' | null
  >(null)

  useEffect(() => {
    let current = true
    request<unknown>('GET', '/api/v1/notifications/web-push').then(
      (body) => {
        if (current && isParentPush(body)) setLoaded(body)
      },
      () => {
        if (current) setLoaded(null)
      },
    )
    return () => {
      current = false
    }
  }, [saved, removed])

  if (!loaded) return null
  const current = loaded

  async function subscribe() {
    setBusy(true)
    setProblem(null)
    setSaved(false)
    setRemoved(false)
    try {
      const keys = await subscribeBrowser(current.publicKey)
      await request('POST', '/api/v1/notifications/web-push', keys)
      setSaved(true)
    } catch (error) {
      setProblem(error instanceof PushUnavailable ? error.reason : 'failed')
    } finally {
      setBusy(false)
    }
  }

  async function remove(id: number) {
    setBusy(true)
    setProblem(null)
    setSaved(false)
    setRemoved(false)
    try {
      await request('DELETE', `/api/v1/notifications/web-push/${id}`)
      setRemoved(true)
    } catch {
      setProblem('failed')
    } finally {
      setBusy(false)
    }
  }

  return (
    <section className="notice" aria-labelledby="browser-push-title">
      <h3 id="browser-push-title">{t('notifications.browserTitle')}</h3>
      <p className="muted">{t('notifications.browserHint')}</p>
      {current.subscriptions.length === 0 && (
        <p>{t('notifications.browserEmpty')}</p>
      )}
      <ul>
        {current.subscriptions.map((browser) => (
          <li key={browser.id}>
            <span>{t('notifications.browserItem', { id: browser.id })}</span>{' '}
            <button
              type="button"
              disabled={busy}
              onClick={() => void remove(browser.id)}
            >
              {t('notifications.browserRemove')}
            </button>
          </li>
        ))}
      </ul>
      <button type="button" disabled={busy} onClick={() => void subscribe()}>
        {t('notifications.browserSubscribe')}
      </button>
      {saved && <p role="status">{t('notifications.browserSaved')}</p>}
      {removed && <p role="status">{t('notifications.browserRemoved')}</p>}
      {problem === 'unsupported' && (
        <p role="alert">{t('webPush.unsupported')}</p>
      )}
      {problem === 'denied' && <p role="alert">{t('webPush.denied')}</p>}
      {problem === 'failed' && (
        <p role="alert">{t('notifications.browserFailed')}</p>
      )}
    </section>
  )
}
