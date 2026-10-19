import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from '@testing-library/react'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import KidProfilesAdmin from './KidProfilesAdmin'
import type { KidProfile } from './kidProfiles'

type Call = { method: string; path: string; body: unknown; headers: Headers }

const ada: KidProfile = {
  id: 1,
  name: 'Ada',
  avatar: 'fox',
  hasPin: true,
  movieAgeLimit: 7,
  tvAgeLimit: 10,
  plexLabel: 'Ada',
  plexUserId: null,
  weeklyAllowance: 3,
  isLocked: false,
}

let calls: Call[]
let profiles: KidProfile[]
let failNext: { status: number; title: string } | null

beforeEach(() => {
  calls = []
  profiles = [ada]
  failNext = null
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
        headers: new Headers(init?.headers),
      })
      if (failNext && method !== 'GET') {
        const failure = failNext
        failNext = null
        return Response.json(
          { title: failure.title },
          { status: failure.status },
        )
      }
      return method === 'GET'
        ? Response.json(profiles)
        : new Response(null, { status: 204 })
    }),
  )
})

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

const writes = () => calls.filter((call) => call.method !== 'GET')

it('lists profiles and offers to unlock only locked ones', async () => {
  profiles = [ada, { ...ada, id: 2, name: 'Ben', isLocked: true }]
  render(<KidProfilesAdmin />)

  expect(await screen.findByText('Ada')).toBeTruthy()
  expect(screen.getByRole('button', { name: 'Unlock Ben' })).toBeTruthy()
  expect(screen.queryByRole('button', { name: 'Unlock Ada' })).toBeNull()

  fireEvent.click(screen.getByRole('button', { name: 'Unlock Ben' }))
  await waitFor(() => expect(writes()).toHaveLength(1))
  expect(writes()[0].method).toBe('POST')
  expect(writes()[0].path).toBe('/api/v1/kid-profiles/2/unlock')
  expect(writes()[0].headers.get('X-CSRF-TOKEN')).toBe('tok')
})

it('creates a profile and leaves the label empty so it defaults to the name', async () => {
  profiles = []
  render(<KidProfilesAdmin />)
  fireEvent.click(await screen.findByRole('button', { name: 'Add profile' }))

  fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Cy' } })
  fireEvent.change(screen.getByLabelText('PIN'), { target: { value: '4821' } })
  fireEvent.change(screen.getByLabelText('Movie age limit'), {
    target: { value: '8' },
  })
  expect(screen.getByLabelText('Plex label').getAttribute('placeholder')).toBe(
    'Cy',
  )
  fireEvent.click(screen.getByRole('button', { name: 'Save' }))

  await waitFor(() => expect(writes()).toHaveLength(1))
  expect(writes()[0].method).toBe('POST')
  expect(writes()[0].path).toBe('/api/v1/kid-profiles')
  expect(writes()[0].body).toMatchObject({
    name: 'Cy',
    pin: '4821',
    movieAgeLimit: 8,
    tvAgeLimit: null,
    plexLabel: '',
  })
})

it('edits a profile without resending an unchanged PIN', async () => {
  render(<KidProfilesAdmin />)
  fireEvent.click(await screen.findByRole('button', { name: 'Edit Ada' }))
  fireEvent.change(screen.getByLabelText('Plex label'), {
    target: { value: 'ada-kids' },
  })
  fireEvent.click(screen.getByRole('button', { name: 'Save' }))

  await waitFor(() => expect(writes()).toHaveLength(1))
  expect(writes()[0].method).toBe('PUT')
  expect(writes()[0].path).toBe('/api/v1/kid-profiles/1')
  expect(writes()[0].body).toMatchObject({
    plexLabel: 'ada-kids',
    removePin: false,
  })
  expect(writes()[0].body).not.toHaveProperty('pin')
})

it('shows the server error when saving fails', async () => {
  render(<KidProfilesAdmin />)
  fireEvent.click(await screen.findByRole('button', { name: 'Edit Ada' }))
  failNext = { status: 400, title: 'Plex labels cannot contain commas' }
  fireEvent.click(screen.getByRole('button', { name: 'Save' }))

  expect((await screen.findByRole('alert')).textContent).toContain(
    'Plex labels cannot contain commas',
  )
})

it('deletes with the label removal option when it is ticked', async () => {
  render(<KidProfilesAdmin />)
  fireEvent.click(await screen.findByRole('button', { name: 'Delete Ada' }))
  fireEvent.click(
    screen.getByLabelText('Also remove the Plex labels this profile owns'),
  )
  fireEvent.click(screen.getByRole('button', { name: 'Confirm delete' }))

  await waitFor(() => expect(writes()).toHaveLength(1))
  expect(writes()[0].method).toBe('DELETE')
  expect(writes()[0].path).toBe('/api/v1/kid-profiles/1?removeOwnedLabels=true')
})

it('deletes without removing labels by default', async () => {
  render(<KidProfilesAdmin />)
  fireEvent.click(await screen.findByRole('button', { name: 'Delete Ada' }))
  expect(screen.getByText(/request history is kept/)).toBeTruthy()
  fireEvent.click(screen.getByRole('button', { name: 'Confirm delete' }))

  await waitFor(() => expect(writes()).toHaveLength(1))
  expect(writes()[0].path).toBe(
    '/api/v1/kid-profiles/1?removeOwnedLabels=false',
  )
})
