import { request, requestWithStatus } from '../api'

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
  needsParent: boolean
}

export type SearchPage = {
  query: string
  page: number
  hasMore: boolean
  results: SearchItem[]
}

export type RequestedTitle = {
  id: number
  state: string
  requestCount: number
  alreadyAsked: boolean
}

export type HomeItem = SearchItem

export type HomeRow = {
  id: string
  genreId: number | null
  genreName: string | null
  items: HomeItem[]
}

export type HomeResponse = { rows: HomeRow[] }

export const getHome = (signal?: AbortSignal) =>
  request<HomeResponse>('GET', '/api/v1/kid/home', undefined, signal)

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

export async function requestTitle(
  kind: SearchItem['kind'],
  tmdbId: number,
): Promise<RequestedTitle> {
  const { status, body } = await requestWithStatus<
    Omit<RequestedTitle, 'alreadyAsked'>
  >('POST', '/api/v1/kid/requests', { kind, tmdbId })
  return { ...body, alreadyAsked: status !== 201 }
}

export const pinLength = { min: 4, max: 8 }
