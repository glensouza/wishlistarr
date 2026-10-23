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
  autoApprove: false,
  autoApproveMovieRating: null,
  autoApproveTvRating: null,
  isLocked: false,
  blockedGenreIds: [],
  blockedKeywordIds: [],
  allowedOriginalLanguages: [],
}

let calls: Call[]
let profiles: KidProfile[]
let failNext: { status: number; title: string } | null
let plexUsers: { id: string; name: string }[] | null

beforeEach(() => {
  calls = []
  profiles = [ada]
  failNext = null
  plexUsers = [
    { id: '10', name: 'Kid Account' },
    { id: '11', name: 'Other' },
  ]
  vi.stubGlobal(
    'fetch',
    vi.fn(async (path: string, init?: RequestInit) => {
      const method = init?.method ?? 'GET'
      if (path === '/api/v1/auth/antiforgery') {
        return Response.json({ token: 'tok', headerName: 'X-CSRF-TOKEN' })
      }
      if (path === '/api/v1/plex/users') {
        return plexUsers
          ? Response.json(plexUsers)
          : Response.json({ title: 'Plex is not linked' }, { status: 409 })
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
    autoApprove: false,
    autoApproveMovieRating: null,
    autoApproveTvRating: null,
  })
})

it('sends content rules and keeps them when a profile is edited', async () => {
  profiles = [
    {
      ...ada,
      blockedGenreIds: [28, 12],
      blockedKeywordIds: [1721],
      allowedOriginalLanguages: ['en', 'ja'],
    },
  ]
  render(<KidProfilesAdmin />)
  fireEvent.click(await screen.findByRole('button', { name: 'Edit Ada' }))

  expect(
    (screen.getByLabelText('Blocked genres') as HTMLInputElement).value,
  ).toBe('28, 12')
  expect(
    (screen.getByLabelText('Blocked keywords') as HTMLInputElement).value,
  ).toBe('1721')
  expect(
    (screen.getByLabelText('Allowed languages') as HTMLInputElement).value,
  ).toBe('en, ja')
  fireEvent.change(screen.getByLabelText('Blocked genres'), {
    target: { value: '53' },
  })
  fireEvent.change(screen.getByLabelText('Allowed languages'), {
    target: { value: 'EN, fr' },
  })
  fireEvent.click(screen.getByRole('button', { name: 'Save' }))

  await waitFor(() => expect(writes()).toHaveLength(1))
  expect(writes()[0].body).toMatchObject({
    blockedGenreIds: [53],
    blockedKeywordIds: [1721],
    allowedOriginalLanguages: ['EN', 'fr'],
  })
})

it('does not save genres that are not ids', async () => {
  render(<KidProfilesAdmin />)
  fireEvent.click(await screen.findByRole('button', { name: 'Edit Ada' }))
  fireEvent.change(screen.getByLabelText('Blocked genres'), {
    target: { value: 'horror' },
  })
  fireEvent.click(screen.getByRole('button', { name: 'Save' }))

  expect((await screen.findByRole('alert')).textContent).toContain('TMDB ids')
  expect(writes()).toHaveLength(0)
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

it('maps a profile to a Plex user chosen from the endpoint', async () => {
  render(<KidProfilesAdmin />)
  fireEvent.click(await screen.findByRole('button', { name: 'Edit Ada' }))
  expect(
    await screen.findByRole('option', { name: 'Kid Account' }),
  ).toBeTruthy()
  fireEvent.change(screen.getByLabelText('Plex user'), {
    target: { value: '10' },
  })
  fireEvent.click(screen.getByRole('button', { name: 'Save' }))

  await waitFor(() => expect(writes()).toHaveLength(1))
  expect(writes()[0].body).toMatchObject({ plexUserId: '10' })
})

it('sends null when no Plex user is selected', async () => {
  profiles = [{ ...ada, plexUserId: '10' }]
  render(<KidProfilesAdmin />)
  fireEvent.click(await screen.findByRole('button', { name: 'Edit Ada' }))
  await screen.findByRole('option', { name: 'Kid Account' })
  expect((screen.getByLabelText('Plex user') as HTMLSelectElement).value).toBe(
    '10',
  )
  fireEvent.change(screen.getByLabelText('Plex user'), {
    target: { value: '' },
  })
  fireEvent.click(screen.getByRole('button', { name: 'Save' }))

  await waitFor(() => expect(writes()).toHaveLength(1))
  expect(writes()[0].body).toMatchObject({ plexUserId: null })
})

it('keeps a mapped id that Plex no longer returns', async () => {
  profiles = [{ ...ada, plexUserId: '99' }]
  render(<KidProfilesAdmin />)
  fireEvent.click(await screen.findByRole('button', { name: 'Edit Ada' }))

  expect(await screen.findByRole('option', { name: '99' })).toBeTruthy()
  expect((screen.getByLabelText('Plex user') as HTMLSelectElement).value).toBe(
    '99',
  )
})

it('still saves when the Plex users cannot be loaded', async () => {
  plexUsers = null
  render(<KidProfilesAdmin />)
  fireEvent.click(await screen.findByRole('button', { name: 'Edit Ada' }))

  expect(await screen.findByText(/Plex users are unavailable/)).toBeTruthy()
  fireEvent.click(screen.getByRole('button', { name: 'Save' }))
  await waitFor(() => expect(writes()).toHaveLength(1))
  expect(writes()[0].body).toMatchObject({ plexUserId: null })
})

it('says so when Plex has no users', async () => {
  plexUsers = []
  render(<KidProfilesAdmin />)
  fireEvent.click(await screen.findByRole('button', { name: 'Edit Ada' }))

  expect(await screen.findByText(/No Plex users found/)).toBeTruthy()
})

it('shows what Plex enforces for a mapped profile and how to fix it', async () => {
  profiles = [
    {
      ...ada,
      plexUserId: '10',
      plexRestrictions: {
        movies: { state: 'enforced', mode: 'allowOnly' },
        television: { state: 'notEnforced', mode: null },
      },
    },
    {
      ...ada,
      id: 2,
      name: 'Ben',
      plexUserId: '11',
      plexRestrictions: {
        movies: { state: 'notEnforced', mode: null },
        television: { state: 'unknown', mode: null },
      },
    },
    { ...ada, id: 3, name: 'Cy' },
  ]
  render(<KidProfilesAdmin />)

  expect(
    await screen.findByText('Plex enforces the label for movies (allow only)'),
  ).toBeTruthy()
  expect(
    screen.getByText(/Plex does not enforce the label for TV shows/),
  ).toBeTruthy()
  expect(
    screen.getByText(/Plex does not enforce the label for movies/),
  ).toBeTruthy()
  expect(
    screen.getByText(
      'Could not tell whether Plex enforces the label for TV shows',
    ),
  ).toBeTruthy()
  expect(screen.getAllByLabelText('Plex label enforcement')).toHaveLength(2)
})

it('round-trips auto-approve rules when a profile is saved', async () => {
  profiles = [
    {
      ...ada,
      autoApprove: true,
      autoApproveMovieRating: 'teen',
      autoApproveTvRating: null,
    },
  ]
  render(<KidProfilesAdmin />)
  fireEvent.click(await screen.findByRole('button', { name: 'Edit Ada' }))

  expect(
    (screen.getByLabelText('Auto-approve requests') as HTMLInputElement)
      .checked,
  ).toBe(true)
  expect(
    (screen.getByLabelText('Movie rating ceiling') as HTMLSelectElement).value,
  ).toBe('teen')
  fireEvent.change(screen.getByLabelText('TV rating ceiling'), {
    target: { value: 'children' },
  })
  fireEvent.click(screen.getByLabelText('Auto-approve requests'))
  fireEvent.click(screen.getByRole('button', { name: 'Save' }))

  await waitFor(() => expect(writes()).toHaveLength(1))
  expect(writes()[0].body).toMatchObject({
    autoApprove: false,
    autoApproveMovieRating: 'teen',
    autoApproveTvRating: 'children',
  })
})
