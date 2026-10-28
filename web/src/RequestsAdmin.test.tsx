import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from '@testing-library/react'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import type { KidProfile } from './kidProfiles'
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
  canRevoke: false,
  labelMayRemain: false,
  canRetry: false,
  dispatchFailure: null,
}

const second: ParentRequest = {
  ...waiting,
  id: 5,
  tmdbId: 701,
  title: 'Movie 701',
}

const ada: KidProfile = {
  id: 1,
  name: 'Ada',
  avatar: 'fox',
  hasPin: false,
  movieAgeLimit: null,
  tvAgeLimit: null,
  plexLabel: 'Ada',
  plexUserId: null,
  weeklyAllowance: null,
  autoApprove: false,
  autoApproveMovieRating: null,
  autoApproveTvRating: null,
  homeRows: ['popular', 'library', 'genre'],
  isLocked: false,
  blockedGenreIds: [],
  blockedKeywordIds: [],
  allowedOriginalLanguages: [],
  blockedDescriptors: [],
  blockedTopics: [],
}

let calls: Call[]
let rows: ParentRequest[]
let labeled: ParentRequest[]
let stuck: ParentRequest[]
let unfinished: number | undefined
let inUse: boolean
let profiles: KidProfile[]
let total: number | undefined
let failing: number[]
let notOwned: boolean
let brokenProfiles: boolean
let breakAt: number | undefined

beforeEach(() => {
  calls = []
  rows = [waiting, second]
  labeled = []
  stuck = []
  unfinished = undefined
  inUse = false
  profiles = []
  total = undefined
  failing = []
  notOwned = false
  brokenProfiles = false
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
      if (method === 'GET' && path.includes('/kid-profiles')) {
        if (brokenProfiles) {
          return Response.json({ title: 'Down' }, { status: 500 })
        }
        return Response.json(profiles)
      }
      if (method === 'GET' && path.includes('/grant/preview')) {
        return path.includes('tmdbId=603')
          ? Response.json({
              kind: 'movie',
              tmdbId: 603,
              title: 'The Matrix',
              inLibrary: true,
            })
          : Response.json({
              kind: 'movie',
              tmdbId: 700,
              title: 'Movie 700',
              inLibrary: false,
            })
      }
      if (method === 'GET') {
        const query = new URL(path, 'http://localhost').searchParams
        const state = query.get('state')
        const offset = Number(query.get('offset') ?? '0')
        const open =
          unfinished ??
          stuck.filter((row) => row.state === 'failed' || row.canRevoke).length
        if (state === 'labeled') {
          return Response.json({
            items: labeled.slice(offset, offset + 100),
            total: labeled.length,
            limit: 100,
            offset,
            unfinished: open,
          })
        }
        if (state === 'failed' || state === 'available') {
          const items = stuck.filter((row) => row.state === state)
          return Response.json({
            items: items.slice(offset, offset + 100),
            total: items.length,
            limit: 100,
            offset,
            unfinished: open,
          })
        }
        return Response.json({
          items: rows,
          total: total ?? rows.length,
          limit: 100,
          offset: 0,
          unfinished: open,
        })
      }
      if (path.endsWith('/grant')) {
        return Response.json({
          id: 9,
          kidProfileId: 1,
          kind: 'movie',
          tmdbId: 603,
          title: 'The Matrix',
          state: 'labeled',
          labelNotOwned: notOwned,
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
          labelNotOwned: notOwned,
          labelInUse: inUse,
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

it('removes a labeled title', async () => {
  labeled = [{ ...waiting, id: 8, state: 'labeled', title: 'Spirited Away' }]
  render(<RequestsAdmin />)
  fireEvent.click(
    await screen.findByRole('button', { name: 'Remove Spirited Away' }),
  )
  await waitFor(() => expect(writes()).toHaveLength(1))
  expect(writes()[0].path).toBe('/api/v1/requests/revoke')
  expect(writes()[0].body).toEqual({ ids: [8], reason: null })
})

it('shows a notice when a revoked label was not added by the app', async () => {
  labeled = [{ ...waiting, id: 8, state: 'labeled', title: 'Spirited Away' }]
  notOwned = true
  render(<RequestsAdmin />)
  fireEvent.click(
    await screen.findByRole('button', { name: 'Remove Spirited Away' }),
  )
  expect(
    await screen.findByText(/its Plex label was not added by Wishlistarr/),
  ).toBeTruthy()
  expect(
    screen.queryByText('Spirited Away was removed from their list.'),
  ).toBeNull()
})

it('looks a title up and gives it after the parent sees its name', async () => {
  profiles = [ada]
  render(<RequestsAdmin />)
  fireEvent.change(await screen.findByLabelText('Profile'), {
    target: { value: '1' },
  })
  fireEvent.change(screen.getByLabelText('TMDB id'), {
    target: { value: '603' },
  })
  expect(screen.queryByRole('button', { name: /^Give / })).toBeNull()
  fireEvent.click(screen.getByRole('button', { name: 'Look up title' }))
  expect(await screen.findByText('The Matrix (Movie)')).toBeTruthy()
  fireEvent.click(screen.getByRole('button', { name: 'Give The Matrix' }))

  await waitFor(() => expect(writes()).toHaveLength(1))
  expect(writes()[0].path).toBe('/api/v1/requests/grant')
  expect(writes()[0].body).toEqual({
    kidProfileId: 1,
    kind: 'movie',
    tmdbId: 603,
  })
  expect(await screen.findByText('The Matrix was given to Ada.')).toBeTruthy()
})

it('says when a granted label was already there', async () => {
  profiles = [ada]
  notOwned = true
  render(<RequestsAdmin />)
  fireEvent.change(await screen.findByLabelText('Profile'), {
    target: { value: '1' },
  })
  fireEvent.change(screen.getByLabelText('TMDB id'), {
    target: { value: '603' },
  })
  fireEvent.click(screen.getByRole('button', { name: 'Look up title' }))
  fireEvent.click(
    await screen.findByRole('button', { name: 'Give The Matrix' }),
  )
  expect(
    await screen.findByText(/was not added by Wishlistarr, so removing it/),
  ).toBeTruthy()
})

it('rejects an invalid TMDB id and a title that is not in the library', async () => {
  profiles = [ada]
  render(<RequestsAdmin />)
  fireEvent.change(await screen.findByLabelText('TMDB id'), {
    target: { value: '6.5' },
  })
  fireEvent.click(screen.getByRole('button', { name: 'Look up title' }))
  expect(await screen.findByText(/Enter a TMDB id/)).toBeTruthy()
  expect(calls.some((call) => call.path.includes('/grant/preview'))).toBe(false)

  fireEvent.change(screen.getByLabelText('Profile'), {
    target: { value: '1' },
  })
  fireEvent.change(screen.getByLabelText('TMDB id'), {
    target: { value: '700' },
  })
  fireEvent.click(screen.getByRole('button', { name: 'Look up title' }))
  expect(await screen.findByText(/Not in the library/)).toBeTruthy()
  const give = screen.getByRole('button', { name: 'Give Movie 700' })
  expect((give as HTMLButtonElement).disabled).toBe(true)
  expect(writes()).toHaveLength(0)
})

it('still lists waiting requests when profiles fail to load, and retries', async () => {
  brokenProfiles = true
  render(<RequestsAdmin />)
  expect(await screen.findByText('Movie 700')).toBeTruthy()
  expect(
    screen.getByText('Some of this page could not be loaded: profiles.'),
  ).toBeTruthy()

  brokenProfiles = false
  profiles = [ada]
  fireEvent.click(screen.getByRole('button', { name: 'Try again' }))
  expect(await screen.findByLabelText('Profile')).toBeTruthy()
  expect(screen.queryByText(/could not be loaded/)).toBeNull()
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

it('loads more titles on their lists past the first page', async () => {
  labeled = Array.from({ length: 130 }, (_, index) => ({
    ...waiting,
    id: 1000 + index,
    state: 'labeled' as const,
    title: `Listed ${index + 1}`,
    canRevoke: true,
  }))
  render(<RequestsAdmin />)
  expect(await screen.findByText('Listed 100')).toBeTruthy()
  expect(screen.queryByText('Listed 101')).toBeNull()
  fireEvent.click(
    screen.getByRole('button', { name: 'Show more (100 of 130 shown)' }),
  )

  expect(await screen.findByText('Listed 130')).toBeTruthy()
  expect(screen.queryByRole('button', { name: /^Show more/ })).toBeNull()
  expect(
    calls.some((call) =>
      call.path.includes('state=labeled&limit=100&offset=100'),
    ),
  ).toBe(true)
  fireEvent.click(screen.getByRole('button', { name: 'Remove Listed 130' }))
  await waitFor(() => expect(writes()).toHaveLength(1))
  expect(writes()[0].body).toEqual({ ids: [1129], reason: null })
})

const unfinishedCalls = () =>
  calls.filter(
    (call) =>
      call.path.includes('state=failed') ||
      call.path.includes('state=available'),
  )

it('offers to remove a title that did not finish only when it can be revoked', async () => {
  stuck = [
    {
      ...waiting,
      id: 20,
      state: 'failed',
      title: 'Kept label',
      canRevoke: true,
      labelMayRemain: true,
    },
    {
      ...waiting,
      id: 21,
      state: 'failed',
      title: 'No label',
      canRevoke: false,
    },
    {
      ...waiting,
      id: 23,
      state: 'failed',
      title: 'Unrecorded label',
      canRevoke: false,
      labelMayRemain: true,
    },
    { ...waiting, id: 22, state: 'available', title: 'Stuck', canRevoke: true },
    {
      ...waiting,
      id: 24,
      state: 'available',
      title: 'Still labeling',
      canRevoke: false,
    },
  ]
  render(<RequestsAdmin />)
  fireEvent.click(await screen.findByRole('button', { name: 'Show them (4)' }))
  expect(
    await screen.findByRole('button', { name: 'Remove Kept label' }),
  ).toBeTruthy()
  expect(screen.getByRole('button', { name: 'Remove Stuck' })).toBeTruthy()
  expect(screen.getByText('No label')).toBeTruthy()
  expect(screen.queryByRole('button', { name: 'Remove No label' })).toBeNull()
  expect(
    screen.queryByRole('button', { name: 'Remove Unrecorded label' }),
  ).toBeNull()
  expect(screen.queryByText('Still labeling')).toBeNull()
  expect(
    screen.getByText(/Removing the title takes it off/).closest('li')
      ?.textContent,
  ).toContain('Kept label')
  expect(
    screen.getByText(/Check the labels on this title in Plex/).closest('li')
      ?.textContent,
  ).toContain('Unrecorded label')
  fireEvent.click(screen.getByRole('button', { name: 'Remove Kept label' }))

  await waitFor(() => expect(writes()).toHaveLength(1))
  expect(writes()[0].path).toBe('/api/v1/requests/revoke')
  expect(writes()[0].body).toEqual({ ids: [20], reason: null })
})

it('loads titles that did not finish only when asked, and not at all when there are none', async () => {
  render(<RequestsAdmin />)
  await screen.findByText('Movie 700')
  expect(screen.queryByText('Did not finish')).toBeNull()
  expect(unfinishedCalls()).toHaveLength(0)
  cleanup()

  stuck = [{ ...waiting, id: 20, state: 'failed', title: 'Kept label' }]
  render(<RequestsAdmin />)
  expect(await screen.findByText('Did not finish')).toBeTruthy()
  expect(screen.getByText(/or a kid asked for stopped part way/)).toBeTruthy()
  expect(screen.queryByText('Kept label')).toBeNull()
  expect(unfinishedCalls()).toHaveLength(0)
  fireEvent.click(screen.getByRole('button', { name: 'Show them (1)' }))

  expect(await screen.findByText('Kept label')).toBeTruthy()
  expect(unfinishedCalls()).toHaveLength(2)
})

it('loads more titles that did not finish past the first page', async () => {
  stuck = [
    ...Array.from({ length: 130 }, (_, index) => ({
      ...waiting,
      id: 2000 + index,
      state: 'failed' as const,
      title: `Unfinished ${index + 1}`,
    })),
    {
      ...waiting,
      id: 3000,
      state: 'available',
      title: 'Stuck',
      canRevoke: true,
    },
  ]
  render(<RequestsAdmin />)
  fireEvent.click(
    await screen.findByRole('button', { name: 'Show them (131)' }),
  )
  expect(await screen.findByText('Unfinished 100')).toBeTruthy()
  expect(screen.queryByText('Unfinished 101')).toBeNull()
  fireEvent.click(
    screen.getByRole('button', { name: 'Show more (101 of 131 shown)' }),
  )

  expect(await screen.findByText('Unfinished 130')).toBeTruthy()
  expect(screen.getByText('Stuck')).toBeTruthy()
  expect(screen.queryByRole('button', { name: /^Show more/ })).toBeNull()
  expect(
    calls.some((call) =>
      call.path.includes('state=failed&limit=100&offset=100'),
    ),
  ).toBe(true)
})

it('says when a revoked label belongs to another profile now', async () => {
  labeled = [
    {
      ...waiting,
      id: 8,
      state: 'labeled',
      title: 'Spirited Away',
      canRevoke: true,
    },
  ]
  inUse = true
  render(<RequestsAdmin />)
  fireEvent.click(
    await screen.findByRole('button', { name: 'Remove Spirited Away' }),
  )
  expect(await screen.findByText(/now belongs to another profile/)).toBeTruthy()
})

it('explains a failed send and retries it only when it can be retried', async () => {
  stuck = [
    {
      ...waiting,
      id: 30,
      state: 'failed',
      title: 'Down backend',
      canRetry: true,
      dispatchFailure: 'unavailable',
    },
    {
      ...waiting,
      id: 31,
      state: 'failed',
      title: 'Not set up',
      canRetry: true,
      dispatchFailure: 'notConfigured',
    },
    { ...waiting, id: 32, state: 'failed', title: 'Label trouble' },
  ]
  render(<RequestsAdmin />)
  fireEvent.click(await screen.findByRole('button', { name: 'Show them (3)' }))

  expect(await screen.findByText(/did not accept it/)).toBeTruthy()
  expect(screen.getByText(/is not set up/)).toBeTruthy()
  expect(
    screen.queryByRole('button', { name: 'Try Label trouble again' }),
  ).toBeNull()
  fireEvent.click(
    screen.getByRole('button', { name: 'Try Down backend again' }),
  )

  expect(
    await screen.findByText('Down backend is being sent again.'),
  ).toBeTruthy()
  expect(writes()).toHaveLength(1)
  expect(writes()[0].path).toBe('/api/v1/requests/retry')
  expect(writes()[0].body).toEqual({ ids: [30], reason: null })
})

it('says when a retry did not go through', async () => {
  stuck = [
    {
      ...waiting,
      id: 33,
      state: 'failed',
      title: 'Stubborn',
      canRetry: true,
      dispatchFailure: 'unavailable',
    },
  ]
  failing = [33]
  render(<RequestsAdmin />)
  fireEvent.click(await screen.findByRole('button', { name: 'Show them (1)' }))
  fireEvent.click(
    await screen.findByRole('button', { name: 'Try Stubborn again' }),
  )

  expect(
    await screen.findByText('That title could not be sent again.'),
  ).toBeTruthy()
})
