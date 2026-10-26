import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import KidHistory from './KidHistory'
import type { KidHistoryEntry } from './kidApi'

let history: KidHistoryEntry[]
let historyDown: boolean

beforeEach(() => {
  history = []
  historyDown = false
  vi.stubGlobal(
    'fetch',
    vi.fn(async (path: string) => {
      if (path === '/api/v1/auth/antiforgery') {
        return Response.json({ token: 'tok', headerName: 'X-CSRF-TOKEN' })
      }
      if (path.startsWith('/api/v1/kid/requests/history')) {
        const query = new URL(path, 'http://localhost').searchParams
        const offset = Number(query.get('offset') ?? '0')
        const limit = Number(query.get('limit') ?? '50')
        return historyDown
          ? Response.json({ title: 'Down' }, { status: 500 })
          : Response.json(history.slice(offset, offset + limit))
      }
      return Response.json([])
    }),
  )
})

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

it('shows a grant and a revocation without a parent account', async () => {
  history = [
    {
      id: 3,
      requestId: 8,
      kind: 'movie',
      tmdbId: 129,
      title: 'Spirited Away',
      action: 'revoked',
      at: '2026-10-22T01:00:00Z',
      note: null,
    },
    {
      id: 1,
      requestId: 8,
      kind: 'movie',
      tmdbId: 129,
      title: 'Spirited Away',
      action: 'granted',
      at: '2026-10-22T00:00:00Z',
      note: 'parent',
    },
  ]
  const { container } = render(<KidHistory />)

  expect(await screen.findByText('A parent added this for you')).toBeTruthy()
  expect(screen.getAllByText('A parent removed this').length).toBeGreaterThan(0)
  expect(screen.queryByText('parent')).toBeNull()
  expect(container.innerHTML).not.toContain('actor')
})

it('words a system approval without mentioning a parent', async () => {
  history = [
    {
      id: 2,
      requestId: 8,
      kind: 'movie',
      tmdbId: 129,
      title: 'Spirited Away',
      action: 'autoApproved',
      at: '2026-10-22T00:00:00Z',
      note: null,
    },
  ]
  render(<KidHistory />)

  expect(await screen.findByText('Approved')).toBeTruthy()
  expect(screen.queryByText('A parent said yes')).toBeNull()
})

it('shows an error when the history fails and retries it', async () => {
  historyDown = true
  render(<KidHistory />)

  expect(
    await screen.findByText('What happened could not be loaded.'),
  ).toBeTruthy()

  historyDown = false
  history = [
    {
      id: 1,
      requestId: 8,
      kind: 'movie',
      tmdbId: 129,
      title: 'Spirited Away',
      action: 'asked',
      at: '2026-10-22T00:00:00Z',
      note: null,
    },
  ]
  fireEvent.click(screen.getByRole('button', { name: 'Try again' }))
  expect(await screen.findByText('You asked for this')).toBeTruthy()
  expect(screen.queryByText('What happened could not be loaded.')).toBeNull()
})

it('shows more history when the first page is full', async () => {
  history = Array.from({ length: 60 }, (_, index) => ({
    id: 100 - index,
    requestId: 8,
    kind: 'movie' as const,
    tmdbId: 129,
    title: `Title ${index + 1}`,
    action: 'asked' as const,
    at: '2026-10-22T00:00:00Z',
    note: null,
  }))
  render(<KidHistory />)
  expect(await screen.findByText('Title 50')).toBeTruthy()
  expect(screen.queryByText('Title 51')).toBeNull()
  fireEvent.click(screen.getByRole('button', { name: 'Show more' }))

  expect(await screen.findByText('Title 60')).toBeTruthy()
  expect(screen.queryByRole('button', { name: 'Show more' })).toBeNull()
})

it('does not offer more history when the first page is not full', async () => {
  history = [
    {
      id: 1,
      requestId: 8,
      kind: 'movie',
      tmdbId: 129,
      title: 'Only one',
      action: 'asked',
      at: '2026-10-22T00:00:00Z',
      note: null,
    },
  ]
  render(<KidHistory />)
  expect(await screen.findByText('Only one')).toBeTruthy()
  expect(screen.queryByRole('button', { name: 'Show more' })).toBeNull()
})
