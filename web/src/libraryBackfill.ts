import { request } from './api'

export type BackfillPreviewItem = {
  libraryItemId: number
  guid: string
  title: string
  kind: 'movie' | 'show'
  tmdbId: number | null
  outcome: 'allowed' | 'needsApproval' | 'blocked'
  reason: { code: string; rating?: string | null }
  selectable: boolean
}

export type BackfillPreview = {
  kidProfileId: number
  serverId: string
  label: string
  items: BackfillPreviewItem[]
  total: number
  offset: number
  limit: number
}

export type BackfillBatch = {
  id: number
  kidProfileId: number
  label: string
  state:
    'applying' | 'paused' | 'completed' | 'undoing' | 'undoPaused' | 'undone'
  error: string | null
  total: number
  pending: number
  applied: number
  preserved: number
  skipped: number
  undone: number
}

export type BackfillDetail = {
  batch: BackfillBatch
  items: { title: string; state: string; detail: string | null }[]
}

const root = '/api/v1/labels/backfill'

export const previewBackfill = (profile: number, offset: number) =>
  request<BackfillPreview>(
    'GET',
    `${root}/preview?kidProfileId=${profile}&offset=${offset}`,
  )

export const listBackfills = () =>
  request<BackfillBatch[]>('GET', `${root}/batches`)

export const getBackfill = (id: number) =>
  request<BackfillDetail>('GET', `${root}/batches/${id}`)

export const applyBackfill = (
  preview: BackfillPreview,
  items: { libraryItemId: number; guid: string }[],
  dryRun: boolean,
) =>
  request<BackfillBatch | { dryRun: true; selected: number }>(
    'POST',
    `${root}/batches`,
    {
      kidProfileId: preview.kidProfileId,
      serverId: preview.serverId,
      label: preview.label,
      items,
      dryRun,
    },
  )

export const changeBackfill = (id: number, action: 'resume' | 'undo') =>
  request<BackfillBatch>('POST', `${root}/batches/${id}/${action}`, {})
