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
}

export type DecisionStatus =
  'succeeded' | 'alreadyDecided' | 'notFound' | 'failed' | 'conflict'

export type DecisionItem = {
  id: number
  status: DecisionStatus
  state: RequestState | null
}

const base = '/api/v1/requests'

export const listRequests = (state: RequestState) =>
  request<ParentRequest[]>('GET', `${base}?state=${state}`)

export const approveRequests = (ids: number[], reason: string | null) =>
  request<{ results: DecisionItem[] }>('POST', `${base}/approve`, {
    ids,
    reason,
  })

export const denyRequests = (ids: number[], reason: string | null) =>
  request<{ results: DecisionItem[] }>('POST', `${base}/deny`, { ids, reason })

export const grantRequest = (
  kidProfileId: number,
  kind: 'movie' | 'show',
  tmdbId: number,
) =>
  request<{ id: number; state: RequestState }>(
    'POST',
    `${base}/grant`,
    { kidProfileId, kind, tmdbId },
  )

export const revokeRequests = (ids: number[]) =>
  request<{ results: DecisionItem[] }>('POST', `${base}/revoke`, {
    ids,
    reason: null,
  })
