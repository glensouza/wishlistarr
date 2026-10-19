import { request } from '../api'

export type PickerProfile = {
  id: number
  name: string
  avatar: string
  requiresPin: boolean
}

export type KidMe = { id: string; name: string }

export const listPickerProfiles = () =>
  request<PickerProfile[]>('GET', '/api/v1/kid/profiles')

export const kidSignIn = (profileId: number, pin?: string) =>
  request<PickerProfile>('POST', '/api/v1/kid/signin', { profileId, pin })

export const getKidMe = () => request<KidMe>('GET', '/api/v1/kid/me')

export const kidSignOut = () => request<void>('POST', '/api/v1/kid/signout')

export type SearchItem = {
  kind: 'movie' | 'show'
  tmdbId: number
  title: string
  year: string | null
  overview: string | null
  posterUrl: string | null
  availability: 'inLibrary' | 'notInLibrary' | null
}

export type SearchPage = {
  query: string
  page: number
  hasMore: boolean
  results: SearchItem[]
}

export type RequestedTitle = { id: number; state: string; requestCount: number }

export const searchTitles = (
  query: string,
  page: number,
  signal: AbortSignal,
) =>
  request<SearchPage>(
    'GET',
    `/api/v1/kid/search?q=${encodeURIComponent(query)}&page=${page}`,
    undefined,
    signal,
  )

export const requestTitle = (kind: SearchItem['kind'], tmdbId: number) =>
  request<RequestedTitle>('POST', '/api/v1/kid/requests', { kind, tmdbId })

export const pinLength = { min: 4, max: 8 }
