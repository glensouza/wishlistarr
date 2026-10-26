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

export type KidWishState = 'waiting' | 'onTheWay' | 'ready' | 'denied'

export type KidPlexLinks = {
  webUrl: string
}

export type KidWish = {
  id: number
  kind: 'movie' | 'show'
  tmdbId: number
  title: string | null
  state: KidWishState
  note: string | null
  plex: KidPlexLinks | null
}

export type KidWishlist = {
  remaining: number | null
  resetsAt: string | null
  items: KidWish[]
}

export const getWishlist = (signal?: AbortSignal) =>
  request<KidWishlist>('GET', '/api/v1/kid/wishlist', undefined, signal)

export type TitleCastMember = {
  name: string
  character: string | null
  profileUrl: string | null
}

export type TitleSeason = {
  number: number
  name: string | null
  overview: string | null
  posterUrl: string | null
  episodeCount: number
  year: string | null
}

export type TitleRequestState = {
  state: string
  requestCount: number
}

export type TitleDetails = {
  kind: SearchItem['kind']
  tmdbId: number
  title: string
  year: string | null
  overview: string | null
  posterUrl: string | null
  backdropUrl: string | null
  runtimeMinutes: number | null
  rating: string | null
  availability: SearchItem['availability']
  needsParent: boolean
  trailerKey: string | null
  cast: TitleCastMember[]
  seasons: TitleSeason[]
  request: TitleRequestState | null
}

export const getTitle = (
  kind: SearchItem['kind'],
  tmdbId: number,
  signal: AbortSignal,
) =>
  request<TitleDetails>(
    'GET',
    `/api/v1/kid/titles/${kind}/${tmdbId}`,
    undefined,
    signal,
  )

export type KidRequest = {
  id: number
  kind: 'movie' | 'show'
  tmdbId: number
  title: string
  state: string
  requestCount: number
  denialReason: string | null
}

export const listKidRequests = () =>
  request<KidRequest[]>('GET', '/api/v1/kid/requests')

export type KidHistoryAction =
  | 'granted'
  | 'asked'
  | 'approved'
  | 'autoApproved'
  | 'denied'
  | 'ready'
  | 'revoked'
  | 'failed'

export type KidHistoryEntry = {
  id: number
  requestId: number
  kind: 'movie' | 'show'
  tmdbId: number
  title: string
  action: KidHistoryAction
  at: string
  note: string | null
}

export const historyPageSize = 50

export const listKidHistory = (offset = 0, limit = historyPageSize) =>
  request<KidHistoryEntry[]>(
    'GET',
    `/api/v1/kid/requests/history?limit=${limit}&offset=${offset}`,
  )

export const pinLength = { min: 4, max: 8 }
