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
let total: number | undefined
let failing: number[]
let breakAt: number | undefined

beforeEach(() => {
  calls = []
  rows = [waiting, second]
  total = undefined
  failing = []
  breakAt = undefined
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
      if (method === 'GET') {
        return Response.json({
          items: rows,
          total: total ?? rows.length,
          limit: 100,
          offset: 0,
        })
      }
      const ids = (JSON.parse(init?.body as string) as { ids: number[] }).ids
      if (ids.includes(breakAt ?? -1)) {
        throw new Error('The connection dropped.')
      }
      return Response.json({
        results: ids.map((id) => ({
          id,
          status: failing.includes(id) ? 'failed' : 'succeeded',
          state: failing.includes(id) ? null : 'approved',
        })),
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

it('splits a large selection into batches and lists what failed', async () => {
  rows = Array.from({ length: 120 }, (_, index) => ({
    ...waiting,
    id: index + 1,
    title: `Movie ${index + 1}`,
  }))
  total = 130
  failing = [60]
  render(<RequestsAdmin />)
  await screen.findByText('Movie 1')
  expect(screen.getByText(/Showing 120 of 130 waiting/)).toBeTruthy()
  fireEvent.click(screen.getByLabelText('Select all shown'))
  fireEvent.click(screen.getByRole('button', { name: 'Approve selected' }))

  await waitFor(() => expect(writes()).toHaveLength(3))
  const sizes = writes().map(
    (call) => (call.body as { ids: number[] }).ids.length,
  )
  expect(sizes).toEqual([50, 50, 20])
  expect(
    await screen.findByText(/Movie 60: Could not be completed/),
  ).toBeTruthy()
})

it('lists the requests that were never sent when a later batch throws', async () => {
  rows = Array.from({ length: 120 }, (_, index) => ({
    ...waiting,
    id: index + 1,
    title: `Movie ${index + 1}`,
  }))
  breakAt = 60
  render(<RequestsAdmin />)
  await screen.findByText('Movie 1')
  fireEvent.click(screen.getByLabelText('Select all shown'))
  fireEvent.click(screen.getByRole('button', { name: 'Approve selected' }))

  expect(await screen.findByText(/The connection dropped/)).toBeTruthy()
  expect(await screen.findByText(/Movie 101: Not sent/)).toBeTruthy()
  expect(screen.getByText(/Movie 120: Not sent/)).toBeTruthy()
  expect(screen.getByText(/Movie 51: No answer/)).toBeTruthy()
  expect(screen.queryByText(/Movie 50: /)).toBeNull()
  expect(writes()).toHaveLength(2)
})
