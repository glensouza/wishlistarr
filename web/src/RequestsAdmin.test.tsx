import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from '@testing-library/react'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import RequestsAdmin from './RequestsAdmin'
import type { ParentRequest } from './requests'

type Call = { method: string; path: string; body: unknown }

const waiting: ParentRequest = {
  id: 4,
  kidProfileId: 1,
  kidName: 'Ada',
  kind: 'movie',
  tmdbId: 700,
  title: 'Movie 700',
  state: 'pending',
  requestCount: 1,
  createdAt: '2026-10-22T00:00:00Z',
  denialReason: null,
}

const second: ParentRequest = {
  ...waiting,
  id: 5,
  tmdbId: 701,
  title: 'Movie 701',
}

let calls: Call[]
let rows: ParentRequest[]

beforeEach(() => {
  calls = []
  rows = [waiting, second]
  vi.stubGlobal(
    'fetch',
    vi.fn(async (path: string, init?: RequestInit) => {
      const method = init?.method ?? 'GET'
      if (path === '/api/v1/auth/antiforgery') {
        return Response.json({ token: 'tok', headerName: 'X-CSRF-TOKEN' })
      }
      calls.push({
        method,
        path,
        body: init?.body ? JSON.parse(init.body as string) : undefined,
      })
      if (method === 'GET') return Response.json(rows)
      return Response.json({
        results: [{ id: 4, status: 'succeeded', state: 'approved' }],
      })
    }),
  )
})

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

const writes = () => calls.filter((call) => call.method !== 'GET')

it('approves one pending request with a trimmed note', async () => {
  render(<RequestsAdmin />)
  expect(await screen.findByText('Movie 700')).toBeTruthy()
  fireEvent.change(screen.getByLabelText('Note for the kid'), {
    target: { value: '  Not tonight  ' },
  })
  fireEvent.click(screen.getByRole('button', { name: 'Approve Movie 700' }))

  await waitFor(() => expect(writes()).toHaveLength(1))
  expect(writes()[0].path).toBe('/api/v1/requests/approve')
  expect(writes()[0].body).toEqual({ ids: [4], reason: 'Not tonight' })
  expect(document.querySelector('b')).toBeNull()
})

it('denies one request and keeps the note as text', async () => {
  render(<RequestsAdmin />)
  await screen.findByText('Movie 701')
  fireEvent.change(screen.getByLabelText('Note for the kid'), {
    target: { value: '<b>Too scary</b>' },
  })
  fireEvent.click(screen.getByRole('button', { name: 'Deny Movie 701' }))

  await waitFor(() => expect(writes()).toHaveLength(1))
  expect(writes()[0].path).toBe('/api/v1/requests/deny')
  expect(writes()[0].body).toEqual({ ids: [5], reason: '<b>Too scary</b>' })
  expect(document.querySelector('b')).toBeNull()
})

it('approves the selected requests together', async () => {
  render(<RequestsAdmin />)
  await screen.findByText('Movie 700')
  fireEvent.click(screen.getByLabelText('Select Movie 700'))
  fireEvent.click(screen.getByLabelText('Select Movie 701'))
  fireEvent.click(screen.getByRole('button', { name: 'Approve selected' }))

  await waitFor(() => expect(writes()).toHaveLength(1))
  expect(writes()[0].path).toBe('/api/v1/requests/approve')
  expect(writes()[0].body).toEqual({ ids: [4, 5], reason: null })
})

it('denies the selected requests together', async () => {
  render(<RequestsAdmin />)
  await screen.findByText('Movie 700')
  fireEvent.click(screen.getByLabelText('Select Movie 700'))
  fireEvent.click(screen.getByLabelText('Select Movie 701'))
  fireEvent.click(screen.getByRole('button', { name: 'Deny selected' }))

  await waitFor(() => expect(writes()).toHaveLength(1))
  expect(writes()[0].path).toBe('/api/v1/requests/deny')
  expect(writes()[0].body).toEqual({ ids: [4, 5], reason: null })
})
