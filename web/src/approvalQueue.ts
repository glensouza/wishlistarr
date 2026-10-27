import { request } from './api'
import type { MessageKey } from './i18n'

export class QueueShapeError extends Error {
  constructor() {
    super('queue-shape')
  }
}

const reasonCodes = [
  'unrated',
  'unknownGenres',
  'revokedBefore',
  'aboveCeiling',
  'autoApproveOff',
  'notAutoApproved',
  'aboveRatingLimit',
  'blockedGenre',
  'blockedKeyword',
  'languageNotAllowed',
  'explicitlyBlocked',
  'unavailable',
] as const

export type QueueReasonCode = (typeof reasonCodes)[number]

const gaps = ['synopsis', 'rating', 'providers', 'trailer', 'keywords'] as const

export type QueueGap = (typeof gaps)[number]

export type QueueReason = {
  code: string
  rating: string | null
  level: string | null
  maxLevel: string | null
  genreName: string | null
  keywordName: string | null
  language: string | null
}

export type QueueItem = {
  id: number
  kidProfileId: number
  kidName: string
  kind: 'movie' | 'show'
  tmdbId: number
  title: string
  requestCount: number
  createdAt: string
  overview: string | null
  rating: string | null
  descriptors: string[]
  providers: { name: string }[]
  trailerKey: string | null
  reason: QueueReason
  gaps: QueueGap[]
}

export type QueuePage = {
  items: QueueItem[]
  total: number
  limit: number
  offset: number
}

type Translate = (
  key: MessageKey,
  params?: Record<string, string | number>,
) => string

const knownReasons = new Set<string>(reasonCodes)

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null
}

function isNullableString(value: unknown): value is string | null {
  return value === null || typeof value === 'string'
}

function isReason(value: unknown): value is QueueReason {
  if (!isRecord(value) || typeof value.code !== 'string') return false
  return (
    isNullableString(value.rating) &&
    isNullableString(value.level) &&
    isNullableString(value.maxLevel) &&
    isNullableString(value.genreName) &&
    isNullableString(value.keywordName) &&
    isNullableString(value.language)
  )
}

function isGap(value: unknown): value is QueueGap {
  return typeof value === 'string' && gaps.some((gap) => gap === value)
}

function withKnownGaps(value: unknown): unknown {
  if (!isRecord(value) || !Array.isArray(value.gaps)) return value
  if (value.gaps.some((gap) => typeof gap !== 'string')) return value
  return {
    ...value,
    gaps: value.gaps.filter((gap): gap is QueueGap => isGap(gap)),
  }
}

function isItem(value: unknown): value is QueueItem {
  if (!isRecord(value)) return false
  return (
    typeof value.id === 'number' &&
    typeof value.kidProfileId === 'number' &&
    typeof value.kidName === 'string' &&
    (value.kind === 'movie' || value.kind === 'show') &&
    typeof value.tmdbId === 'number' &&
    typeof value.title === 'string' &&
    typeof value.requestCount === 'number' &&
    typeof value.createdAt === 'string' &&
    isNullableString(value.overview) &&
    isNullableString(value.rating) &&
    Array.isArray(value.descriptors) &&
    value.descriptors.every((item) => typeof item === 'string') &&
    Array.isArray(value.providers) &&
    value.providers.every(
      (provider) => isRecord(provider) && typeof provider.name === 'string',
    ) &&
    isNullableString(value.trailerKey) &&
    isReason(value.reason) &&
    Array.isArray(value.gaps) &&
    value.gaps.every(isGap)
  )
}

export function readQueue(body: unknown): QueuePage {
  if (
    !isRecord(body) ||
    !Array.isArray(body.items) ||
    typeof body.total !== 'number' ||
    typeof body.limit !== 'number' ||
    typeof body.offset !== 'number'
  ) {
    throw new QueueShapeError()
  }
  const items = body.items.map(withKnownGaps)
  if (!items.every(isItem)) throw new QueueShapeError()
  return {
    items,
    total: body.total,
    limit: body.limit,
    offset: body.offset,
  }
}

export function listQueue(kidProfileId?: number) {
  const path =
    kidProfileId === undefined
      ? '/api/v1/requests/queue'
      : `/api/v1/requests/queue?kidProfileId=${kidProfileId}`
  return request<unknown>('GET', path).then(readQueue)
}

export function reasonText(reason: QueueReason, t: Translate) {
  const code = knownReasons.has(reason.code) ? reason.code : 'unavailable'
  switch (code) {
    case 'unrated':
      return t('queue.reasonUnrated')
    case 'unknownGenres':
      return t('queue.reasonUnknownGenres')
    case 'revokedBefore':
      return t('queue.reasonRevoked')
    case 'aboveCeiling':
      return reason.rating
        ? t('queue.reasonAboveCeiling', { rating: reason.rating })
        : t('queue.reasonAboveCeilingUnrated')
    case 'autoApproveOff':
      return reason.rating
        ? t('queue.reasonAutoOff', { rating: reason.rating })
        : t('queue.reasonAutoOffUnrated')
    case 'notAutoApproved':
      return t('queue.reasonNotAuto')
    case 'aboveRatingLimit':
      return reason.rating
        ? t('queue.reasonAboveAge', { rating: reason.rating })
        : t('queue.reasonUnavailable')
    case 'blockedGenre':
      return reason.genreName
        ? t('queue.reasonGenre', { name: reason.genreName })
        : t('queue.reasonUnavailable')
    case 'blockedKeyword':
      return reason.keywordName
        ? t('queue.reasonKeyword', { name: reason.keywordName })
        : t('queue.reasonUnavailable')
    case 'languageNotAllowed':
      return reason.language
        ? t('queue.reasonLanguage', { language: reason.language })
        : t('queue.reasonLanguageMissing')
    case 'explicitlyBlocked':
      return t('queue.reasonBlocked')
    default:
      return t('queue.reasonUnavailable')
  }
}
