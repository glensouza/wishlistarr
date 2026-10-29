import { useEffect, useState } from 'react'
import { request } from '../api'
import { useT } from '../i18n'
import { setupError } from './setupError'

type Server = { id: string; name: string }
type Status = { linked: boolean; server: Server | null }
type Link = { id: string; authUrl: string }

export default function PlexSetupStep({ onNext }: { onNext: () => void }) {
  const t = useT()
  const [linked, setLinked] = useState(false)
  const [servers, setServers] = useState<Server[]>([])
  const [serverId, setServerId] = useState('')
  const [link, setLink] = useState<Link | null>(null)
  const [busy, setBusy] = useState(true)
  const [valid, setValid] = useState(false)
  const [message, setMessage] = useState('')
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let current = true
    async function load() {
      try {
        const status = await request<Status>('GET', '/api/v1/plex/status')
        if (current) setLinked(status.linked)
        const discovered = status.linked
          ? await request<Server[]>('GET', '/api/v1/plex/servers')
          : []
        if (!current) return
        setLinked(status.linked)
        setServers(discovered)
        setServerId(status.server?.id ?? '')
      } catch (failure) {
        if (current) setError(setupError(failure, t('setup.plexFailed')))
      } finally {
        if (current) setBusy(false)
      }
    }
    void load()
    return () => {
      current = false
    }
  }, [t])

  async function run(action: () => Promise<void>) {
    setBusy(true)
    setValid(false)
    setError(null)
    setMessage('')
    try {
      await action()
    } catch (failure) {
      setError(setupError(failure, t('setup.plexFailed')))
    } finally {
      setBusy(false)
    }
  }
  return (
    <>
      <p>{t('setup.plexHint')}</p>
      <fieldset disabled={busy}>
        <button
          type="button"
          onClick={() =>
            void run(async () => {
              setLink(null)
              setLink(await request<Link>('POST', '/api/v1/plex/link'))
            })
          }
        >
          {t('setup.linkPlex')}
        </button>
        {link && (
          <>
            <p>{t('setup.approvePlex')}</p>
            <code>{link.authUrl}</code>
            <button
              type="button"
              onClick={() =>
                void run(async () => {
                  const result = await request<{ state: string }>(
                    'POST',
                    `/api/v1/plex/link/${encodeURIComponent(link.id)}/check`,
                  )
                  if (result.state !== 'linked') {
                    setError(
                      t(
                        result.state === 'expired'
                          ? 'setup.linkExpired'
                          : 'setup.linkPending',
                      ),
                    )
                    return
                  }
                  setLinked(true)
                  setServerId('')
                  setLink(null)
                  setServers(
                    await request<Server[]>('GET', '/api/v1/plex/servers'),
                  )
                })
              }
            >
              {t('setup.checkLink')}
            </button>
          </>
        )}
        {linked && (
          <>
            <label>
              {t('setup.server')}
              <select
                value={serverId}
                onChange={(event) => {
                  setServerId(event.target.value)
                  setValid(false)
                  setMessage('')
                }}
              >
                <option value="">{t('setup.choose')}</option>
                {servers.map((server) => (
                  <option key={server.id} value={server.id}>
                    {server.name}
                  </option>
                ))}
              </select>
            </label>
            {servers.length === 0 && <p>{t('setup.noServers')}</p>}
            <button
              type="button"
              onClick={() =>
                void run(async () => {
                  setServers(
                    await request<Server[]>('GET', '/api/v1/plex/servers'),
                  )
                })
              }
            >
              {t('setup.refreshServers')}
            </button>
            <button
              type="button"
              disabled={!serverId}
              onClick={() =>
                void run(async () => {
                  await request('PUT', '/api/v1/plex/server', { serverId })
                  const result = await request<{
                    valid: boolean
                    message: string
                  }>('POST', '/api/v1/settings/plex/test')
                  setValid(result.valid === true)
                  if (result.valid) setMessage(result.message)
                  else setError(result.message)
                })
              }
            >
              {t('setup.test')}
            </button>
          </>
        )}
      </fieldset>
      {busy && <p role="status">{t('setup.checking')}</p>}
      {message && <p role="status">{message}</p>}
      {error && <p role="alert">{error}</p>}
      <button type="button" disabled={!valid || busy} onClick={onNext}>
        {t('setup.next')}
      </button>
    </>
  )
}
