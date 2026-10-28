import { useEffect, useState } from 'react'
import { request } from '../api'
import { useT } from '../i18n'
import { PushUnavailable, subscribeBrowser } from '../webPush'

type DevicePushState = { publicKey: string; subscribed: boolean }

function isDevicePush(value: unknown): value is DevicePushState {
  if (!value || typeof value !== 'object') return false
  const body = value as { publicKey?: unknown; subscribed?: unknown }
  return (
    typeof body.publicKey === 'string' && typeof body.subscribed === 'boolean'
  )
}

export default function DevicePush() {
  const t = useT()
  const [loaded, setLoaded] = useState<DevicePushState | null>(null)
  const [version, setVersion] = useState(0)
  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState(false)
  const [removed, setRemoved] = useState(false)
  const [problem, setProblem] = useState<
    'unsupported' | 'denied' | 'failed' | null
  >(null)

  useEffect(() => {
    let current = true
    request<unknown>('GET', '/api/v1/kid/web-push').then(
      (body) => {
        if (current) setLoaded(isDevicePush(body) ? body : null)
      },
      () => {
        if (current) setLoaded(null)
      },
    )
    return () => {
      current = false
    }
  }, [version])

  if (!loaded) return null
  const current = loaded

  async function subscribe() {
    setBusy(true)
    setProblem(null)
    setSaved(false)
    setRemoved(false)
    try {
      const keys = await subscribeBrowser(current.publicKey)
      await request('POST', '/api/v1/kid/web-push', keys)
      setSaved(true)
      setVersion((value) => value + 1)
    } catch (error) {
      setProblem(error instanceof PushUnavailable ? error.reason : 'failed')
    } finally {
      setBusy(false)
    }
  }

  async function remove() {
    setBusy(true)
    setProblem(null)
    setSaved(false)
    setRemoved(false)
    try {
      await request('DELETE', '/api/v1/kid/web-push')
      setRemoved(true)
      setVersion((value) => value + 1)
    } catch {
      setProblem('failed')
    } finally {
      setBusy(false)
    }
  }

  const action = current.subscribed
    ? t('devicePush.remove')
    : t('devicePush.subscribe')

  return (
    <div className="notice">
      <p>{current.subscribed ? t('devicePush.on') : t('devicePush.prompt')}</p>
      <button
        type="button"
        disabled={busy}
        onClick={() => void (current.subscribed ? remove() : subscribe())}
      >
        {action}
      </button>
      {saved && <p role="status">{t('devicePush.saved')}</p>}
      {removed && <p role="status">{t('devicePush.removed')}</p>}
      {problem === 'unsupported' && (
        <p role="alert">{t('webPush.unsupported')}</p>
      )}
      {problem === 'denied' && <p role="alert">{t('webPush.denied')}</p>}
      {problem === 'failed' && <p role="alert">{t('devicePush.failed')}</p>}
    </div>
  )
}
