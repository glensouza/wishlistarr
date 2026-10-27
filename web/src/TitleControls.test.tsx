import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import TitleControls from './TitleControls'
import { exportRules, importRules, validTitles } from './contentRules'

const title = {
  kind: 'movie' as const,
  tmdbId: 603,
  title: 'The Matrix',
  blocked: true,
}
afterEach(() => {
  vi.useRealTimers()
  cleanup()
  vi.unstubAllGlobals()
})

it('searches the server list and previews the decision and reason again immediately after a change', async () => {
  let blocked = false
  const calls: string[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (path: string, init?: RequestInit) => {
      calls.push(path)
      if (path.includes('/antiforgery'))
        return Response.json({ token: 'token', headerName: 'X-CSRF-TOKEN' })
      if (path.includes('/title-search')) return Response.json([title])
      if (path.endsWith('/preview'))
        return Response.json({
          decision: blocked ? 'blocked' : 'allowed',
          reason: blocked
            ? 'This title is on the blocked list.'
            : 'This title meets the rules.',
        })
      if (init?.method === 'POST') {
        blocked = true
        return new Response(null, { status: 204 })
      }
      if (init?.method === 'DELETE') {
        blocked = false
        return new Response(null, { status: 204 })
      }
      return Response.json({
        total: blocked ? 1 : 0,
        items: blocked ? [title] : [],
      })
    }),
  )
  render(<TitleControls profileId={7} disabled={false} />)
  fireEvent.change(screen.getByLabelText('Search this title list'), {
    target: { value: 'matrix' },
  })
  await waitFor(() =>
    expect(
      calls.some((path) =>
        path.includes('blocked=true&q=matrix&limit=20&offset=0'),
      ),
    ).toBe(true),
  )
  fireEvent.change(screen.getByLabelText('Find a title'), {
    target: { value: 'matrix' },
  })
  fireEvent.click(screen.getByRole('button', { name: 'Search titles' }))
  fireEvent.click(
    await screen.findByRole('button', { name: 'The Matrix (Movie)' }),
  )
  expect((await screen.findByRole('status')).textContent).toBe(
    'Allowed: This title meets the rules.',
  )
  fireEvent.click(screen.getByRole('button', { name: 'Block title' }))
  await waitFor(() =>
    expect(screen.getByRole('status').textContent).toBe(
      'Blocked: This title is on the blocked list.',
    ),
  )
  fireEvent.click(screen.getByRole('button', { name: 'Remove The Matrix' }))
  await waitFor(() =>
    expect(screen.getByRole('status').textContent).toContain('Allowed:'),
  )
  expect(calls.some((path) => path.includes('/7/preview'))).toBe(true)
})

it('round trips both title lists and rejects missing lists, invalid keys and duplicates', () => {
  const rules = {
    movieAgeLimit: 7,
    tvAgeLimit: null,
    weeklyAllowance: 3,
    autoApprove: false,
    autoApproveMovieRating: null,
    autoApproveTvRating: null,
    blockedGenreIds: [],
    blockedKeywordIds: [],
    allowedOriginalLanguages: [],
  }
  const titles = [title, { ...title, kind: 'show' as const, blocked: false }]
  const text = exportRules(rules, titles)
  expect(JSON.parse(text).titles).toEqual(titles)
  expect(importRules(text)).toEqual(rules)
  expect(validTitles([title, title])).toBe(false)
  expect(validTitles([{ ...title, tmdbId: 0 }])).toBe(false)
  expect(importRules(JSON.stringify({ version: 1, rules }))).toBeNull()
  expect(
    importRules(
      JSON.stringify({ version: 1, rules, titles: [{ ...title, title: '' }] }),
    ),
  ).toBeNull()
})

it('submits title search with Enter and explains the preview limits', async () => {
  const fetch = vi.fn(async (path: string) => {
    if (path.includes('/antiforgery'))
      return Response.json({ token: 'token', headerName: 'X-CSRF-TOKEN' })
    if (path.includes('/title-search')) return Response.json([title])
    if (path.endsWith('/preview'))
      return Response.json({ decision: 'allowed', reason: 'Allowed' })
    return Response.json({ total: 0, items: [] })
  })
  vi.stubGlobal('fetch', fetch)
  render(<TitleControls profileId={7} disabled={false} />)
  fireEvent.change(screen.getByLabelText('Find a title'), {
    target: { value: 'matrix' },
  })
  fireEvent.submit(screen.getByLabelText('Find a title').closest('form')!)
  fireEvent.click(
    await screen.findByRole('button', { name: 'The Matrix (Movie)' }),
  )
  expect(
    await screen.findByText(/The real request may still wait for a parent/),
  ).toBeTruthy()
  expect(
    fetch.mock.calls.some(([path]) => path.includes('/title-search?q=matrix')),
  ).toBe(true)
})

it('debounces filters for 300 ms, aborts old lookups and ignores stale responses', async () => {
  let stale: ((response: Response) => void) | undefined
  let staleSignal: AbortSignal | undefined
  const fetch = vi.fn((path: string, init?: RequestInit) => {
    if (path.includes('q=first&')) {
      staleSignal = init?.signal as AbortSignal
      return new Promise<Response>((resolve) => {
        stale = resolve
      })
    }
    return Promise.resolve(
      Response.json({
        total: path.includes('q=second&') ? 1 : 0,
        items: path.includes('q=second&')
          ? [{ ...title, title: 'Current title' }]
          : [],
      }),
    )
  })
  vi.stubGlobal('fetch', fetch)
  render(<TitleControls profileId={7} disabled={false} />)
  await waitFor(() => expect(fetch).toHaveBeenCalledOnce())
  vi.useFakeTimers()
  fireEvent.change(screen.getByLabelText('Search this title list'), {
    target: { value: 'f' },
  })
  await act(() => vi.advanceTimersByTimeAsync(200))
  fireEvent.change(screen.getByLabelText('Search this title list'), {
    target: { value: 'first' },
  })
  await act(() => vi.advanceTimersByTimeAsync(299))
  expect(fetch).toHaveBeenCalledTimes(1)
  await act(() => vi.advanceTimersByTimeAsync(1))
  expect(fetch).toHaveBeenCalledTimes(2)
  fireEvent.change(screen.getByLabelText('Search this title list'), {
    target: { value: 'second' },
  })
  expect(staleSignal?.aborted).toBe(true)
  await act(() => vi.advanceTimersByTimeAsync(300))
  expect(screen.getByText(/Current title/)).toBeTruthy()
  await act(async () => {
    stale!(
      Response.json({ total: 1, items: [{ ...title, title: 'Stale title' }] }),
    )
  })
  expect(screen.queryByText(/Stale title/)).toBeNull()
  expect(screen.getByText(/Current title/)).toBeTruthy()
})

it.each([
  [409, 'This title is already on the list.'],
  [404, 'The profile or title was not found.'],
  [500, 'The server could not complete the request. Try again.'],
  [0, 'Could not connect. Check your connection and try again.'],
])('shows the specific title error for %s', async (status, message) => {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (path: string) => {
      if (path.includes('/title-search')) {
        if (status === 0) throw new TypeError('Failed to fetch')
        return Response.json({}, { status })
      }
      return Response.json({ total: 0, items: [] })
    }),
  )
  render(<TitleControls profileId={7} disabled={false} />)
  fireEvent.change(screen.getByLabelText('Find a title'), {
    target: { value: 'matrix' },
  })
  fireEvent.click(screen.getByRole('button', { name: 'Search titles' }))
  expect((await screen.findByRole('alert')).textContent).toContain(message)
})
