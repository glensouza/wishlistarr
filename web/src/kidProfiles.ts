import { request } from './api'

export type KidProfile = {
  id: number
  name: string
  avatar: string
  hasPin: boolean
  movieAgeLimit: number | null
  tvAgeLimit: number | null
  plexLabel: string
  plexUserId: string | null
  weeklyAllowance: number | null
  isLocked: boolean
  blockedGenreIds: number[]
  blockedKeywordIds: number[]
  allowedOriginalLanguages: string[]
  homeRows: string[]
  plexRestrictions?: PlexRestrictions | null
}

export type RestrictionState = 'enforced' | 'notEnforced' | 'unknown'

export type LabelRestriction = {
  state: RestrictionState
  mode: 'allowOnly' | null
}

export type PlexRestrictions = {
  movies: LabelRestriction
  television: LabelRestriction
}

export type HomeOptions = {
  defaultRows: string[]
  genres: { id: number; name: string }[]
}

export type KidProfileInput = {
  name: string
  avatar: string
  pin?: string
  removePin?: boolean
  movieAgeLimit: number | null
  tvAgeLimit: number | null
  plexLabel: string
  plexUserId: string | null
  weeklyAllowance: number | null
  blockedGenreIds: number[]
  blockedKeywordIds: number[]
  allowedOriginalLanguages: string[]
  homeRows: string[]
}

const base = '/api/v1/kid-profiles'

export const getHomeOptions = () =>
  request<HomeOptions>('GET', `${base}/home-options`)

export const listKidProfiles = () => request<KidProfile[]>('GET', base)

export const createKidProfile = (input: KidProfileInput) =>
  request<KidProfile>('POST', base, input)

export const updateKidProfile = (id: number, input: KidProfileInput) =>
  request<KidProfile>('PUT', `${base}/${id}`, input)

export const deleteKidProfile = (id: number, removeOwnedLabels: boolean) =>
  request<void>(
    'DELETE',
    `${base}/${id}?removeOwnedLabels=${removeOwnedLabels}`,
  )

export const unlockKidProfile = (id: number) =>
  request<void>('POST', `${base}/${id}/unlock`)

export { avatars } from './kid/avatarArt'
