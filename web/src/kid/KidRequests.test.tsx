import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import KidRequests from './KidRequests'
import type { KidRequest } from './kidApi'

const reason = '<img src=x onerror=alert(1)>'

const denied: KidRequest = {
  id: 8,
  kind: 'movie',
  tmdbId: 129,
  title: 'Spirited Away',
  state: 'denied',
  requestCount: 1,
  denialReason: reason,
}

let rows: KidRequest[]

beforeEach(() => {
  rows = [denied]
  vi.stubGlobal(
    'fetch',
    vi.fn(async (path: string) => {
      if (path === '/api/v1/auth/antiforgery') {
        return Response.json({ token: 'tok', headerName: 'X-CSRF-TOKEN' })
      }
      return Response.json(rows)
    }),
  )
})

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

it('shows a denial reason as text and does not build elements from it', async () => {
  const { container } = render(<KidRequests />)

  expect(await screen.findByText(reason)).toBeTruthy()
  expect(screen.getByText('Not this time')).toBeTruthy()
  expect(container.querySelector('img')).toBeNull()
  expect(container.innerHTML).not.toContain('<img')
})

it('does not show a failure explanation to the kid', async () => {
  rows = [
    {
      ...denied,
      state: 'failed',
      denialReason: 'The title is no longer in the library.',
    },
  ]
  render(<KidRequests />)

  expect(await screen.findByText('Something went wrong')).toBeTruthy()
  expect(screen.queryByText(/library/i)).toBeNull()
  expect(screen.queryByText(/label/i)).toBeNull()
})
