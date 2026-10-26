import { request } from './api'

export type RequestState =
  | 'pending'
  | 'approved'
  | 'denied'
  | 'fulfilling'
  | 'available'
  | 'labeled'
  | 'failed'
  | 'revoked'

export type ParentRequest = {
  id: number
  kidProfileId: number
  kidName: string
  kind: 'movie' | 'show'
  tmdbId: number
  title: string
  state: RequestState
  requestCount: number
  createdAt: string
  denialReason: string | null
  canRevoke: boolean
}

export type DecisionStatus =
  | 'succeeded'
  | 'alreadyDecided'
  | 'notFound'
  | 'failed'
  | 'conflict'
  | 'notProcessed'

export type DecisionItem = {
  id: number
  status: DecisionStatus
  state: RequestState | null
}

const base = '/api/v1/requests'

export type RequestPage = {
  items: ParentRequest[]
  total: number
  limit: number
  offset: number
}

export const pageSize = 100
export const maxBatch = 50

export const listRequests = (
  state: RequestState,
  limit = pageSize,
  offset = 0,
) =>
  request<RequestPage>(
    'GET',
    `${base}?state=${state}&limit=${limit}&offset=${offset}`,
  )

export const approveRequests = (ids: number[], reason: string | null) =>
  request<{ results: DecisionItem[] }>('POST', `${base}/approve`, {
    ids,
    reason,
  })

export const denyRequests = (ids: number[], reason: string | null) =>
  request<{ results: DecisionItem[] }>('POST', `${base}/deny`, { ids, reason })

export type GrantResult = {
  id: number
  kidProfileId: number
  kind: 'movie' | 'show'
  tmdbId: number
  title: string
  state: RequestState
  labelNotOwned: boolean
}

export const grantRequest = (
  kidProfileId: number,
  kind: 'movie' | 'show',
  tmdbId: number,
) =>
  request<GrantResult>('POST', `${base}/grant`, { kidProfileId, kind, tmdbId })

export type GrantPreview = {
  kind: 'movie' | 'show'
  tmdbId: number
  title: string
  inLibrary: boolean | null
}

export const previewGrant = (kind: 'movie' | 'show', tmdbId: number) =>
  request<GrantPreview>(
    'GET',
    `${base}/grant/preview?kind=${kind}&tmdbId=${tmdbId}`,
  )

export type RevokeItem = DecisionItem & {
  labelNotOwned: boolean
  labelInUse: boolean
}

export const revokeRequests = (ids: number[]) =>
  request<{ results: RevokeItem[] }>('POST', `${base}/revoke`, {
    ids,
    reason: null,
  })
