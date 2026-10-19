import { useEffect, useState } from 'react'
import { request } from './api'

type PlexUser = { id: string; name: string }

type Props = { value: string; onChange: (value: string) => void }

export default function PlexUserPicker({ value, onChange }: Props) {
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
      Plex user
      <select
        value={value}
        disabled={users === null && !failed}
        onChange={(event) => onChange(event.target.value)}
      >
        <option value="">None</option>
        {value !== '' && !known && <option value={value}>{value}</option>}
        {users?.map((user) => (
          <option key={user.id} value={user.id}>
            {user.name}
          </option>
        ))}
      </select>
      {users === null && !failed && <span> Loading Plex users</span>}
      {failed && (
        <span> Plex users are unavailable. Plex may not be connected.</span>
      )}
      {users?.length === 0 && <span> No Plex users found.</span>}
    </label>
  )
}
