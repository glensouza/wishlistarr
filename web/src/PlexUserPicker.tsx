import { useEffect, useState } from 'react'
import { request } from './api'
import { useT } from './i18n'

type PlexUser = { id: string; name: string }

type Props = { value: string; onChange: (value: string) => void }

export default function PlexUserPicker({ value, onChange }: Props) {
  const t = useT()
  const [users, setUsers] = useState<PlexUser[] | null>(null)
  const [failed, setFailed] = useState(false)

  useEffect(() => {
    let current = true
    request<PlexUser[]>('GET', '/api/v1/plex/users').then(
      (loaded) => current && setUsers(loaded),
      () => current && setFailed(true),
    )
    return () => {
      current = false
    }
  }, [])

  const known = users?.some((user) => user.id === value) ?? false

  return (
    <label>
      {t('plex.user')}
      <select
        value={value}
        disabled={users === null && !failed}
        onChange={(event) => onChange(event.target.value)}
      >
        <option value="">{t('plex.none')}</option>
        {value !== '' && !known && <option value={value}>{value}</option>}
        {users?.map((user) => (
          <option key={user.id} value={user.id}>
            {user.name}
          </option>
        ))}
      </select>
      {users === null && !failed && <span> {t('plex.loading')}</span>}
      {failed && <span> {t('plex.unavailable')}</span>}
      {users?.length === 0 && <span> {t('plex.empty')}</span>}
    </label>
  )
}
