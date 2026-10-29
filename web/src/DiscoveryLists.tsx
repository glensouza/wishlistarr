import { useEffect, useState } from 'react'
import { request } from './api'
import { useT } from './i18n'

type List = { provider: string; listId: number; name: string }
type View = { lists: List[]; hasTraktKey: boolean; hasMdbListKey: boolean }

export default function DiscoveryLists() {
  const t = useT()
  const [lists, setLists] = useState<List[]>([])
  const [traktKey, setTraktKey] = useState('')
  const [mdbListKey, setMdbListKey] = useState('')
  const [loaded, setLoaded] = useState(false)
  const [message, setMessage] = useState('')

  useEffect(() => {
    const controller = new AbortController()
    request<View>(
      'GET',
      '/api/v1/settings/discovery-lists',
      undefined,
      controller.signal,
    ).then(
      (view) => {
        if (controller.signal.aborted) return
        if (!view || !Array.isArray(view.lists)) {
          setMessage(t('lists.loadFailed'))
          return
        }
        setLists(view.lists)
        setLoaded(true)
      },
      () => {
        if (!controller.signal.aborted) setMessage(t('lists.loadFailed'))
      },
    )
    return () => controller.abort()
  }, [t])

  function change(index: number, patch: Partial<List>) {
    setLists((current) =>
      current.map((item, i) => (i === index ? { ...item, ...patch } : item)),
    )
  }

  async function save(event: React.FormEvent) {
    event.preventDefault()
    try {
      await request('PUT', '/api/v1/settings/discovery-lists', {
        lists,
        traktKey,
        mdbListKey,
      })
      setTraktKey('')
      setMdbListKey('')
      setMessage(t('lists.saved'))
    } catch {
      setMessage(t('lists.saveFailed'))
    }
  }

  return (
    <section>
      <h2>{t('lists.title')}</h2>
      <p>{t('lists.hint')}</p>
      {message && <p role="status">{message}</p>}
      {loaded && (
        <form onSubmit={(event) => void save(event)}>
          {lists.map((list, index) => (
            <fieldset key={index}>
              <legend>{t('lists.item', { number: index + 1 })}</legend>
              <label>
                {t('lists.provider')}{' '}
                <select
                  value={list.provider}
                  onChange={(event) =>
                    change(index, { provider: event.target.value })
                  }
                >
                  <option value="trakt">Trakt</option>
                  <option value="mdblist">MDBList</option>
                  <option value="mdblist-external">
                    {t('lists.external')}
                  </option>
                </select>
              </label>
              <label>
                {t('lists.id')}{' '}
                <input
                  required
                  type="number"
                  min="1"
                  value={list.listId || ''}
                  onChange={(event) =>
                    change(index, { listId: Number(event.target.value) })
                  }
                />
              </label>
              <label>
                {t('lists.name')}{' '}
                <input
                  required
                  maxLength={80}
                  value={list.name}
                  onChange={(event) =>
                    change(index, { name: event.target.value })
                  }
                />
              </label>
              <button
                type="button"
                onClick={() => setLists(lists.filter((_, i) => i !== index))}
              >
                {t('lists.remove')}
              </button>
            </fieldset>
          ))}
          <button
            type="button"
            disabled={lists.length >= 5}
            onClick={() =>
              setLists([...lists, { provider: 'trakt', listId: 0, name: '' }])
            }
          >
            {t('lists.add')}
          </button>
          <p>{t('lists.keyHint')}</p>
          <label>
            {t('lists.traktKey')}{' '}
            <input
              type="password"
              maxLength={200}
              value={traktKey}
              onChange={(event) => setTraktKey(event.target.value)}
            />
          </label>
          <label>
            {t('lists.mdbKey')}{' '}
            <input
              type="password"
              maxLength={200}
              value={mdbListKey}
              onChange={(event) => setMdbListKey(event.target.value)}
            />
          </label>
          <button type="submit">{t('lists.save')}</button>
        </form>
      )}
    </section>
  )
}
