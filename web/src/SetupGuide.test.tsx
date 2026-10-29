import { cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, expect, it, vi } from 'vitest'
import SetupGuide from './SetupGuide'
import { ApiError, request } from './api'

vi.mock('./api', () => ({
  request: vi.fn(),
  ApiError: class extends Error {
    status: number
    constructor(status: number, message: string) {
      super(message)
      this.status = status
    }
  },
}))

const call = vi.mocked(request)
let restricted = false
let seerrValid = false
let complete = false
let showGuide = true
let statusFails = false
const kid = () => ({
  id: 1,
  name: 'Demo',
  plexLabel: 'Demo',
  plexUserId: '123',
  plexRestrictions: {
    movies: { state: 'notEnforced', restricted },
    television: { state: 'notEnforced', restricted },
  },
})

function install() {
  call.mockImplementation(async (method, path, body) => {
    if (path === '/api/v1/setup') {
      if (statusFails) throw new Error('offline')
      return { completed: complete, showGuide } as never
    }
    if (path === '/api/v1/setup/dismiss') return undefined as never
    if (path === '/api/v1/plex/status')
      return { linked: true, server: { id: 'server', name: 'Server' } } as never
    if (path === '/api/v1/plex/servers')
      return [{ id: 'server', name: 'Server' }] as never
    if (path === '/api/v1/plex/server') return undefined as never
    if (path === '/api/v1/settings/plex/test')
      return { valid: true, message: 'Connected' } as never
    if (path === '/api/v1/settings' && method === 'GET') return [] as never
    if (path === '/api/v1/settings/tmdb.api_key') return undefined as never
    if (path === '/api/v1/settings/tmdb/test-candidate') {
      const valid = (body as { apiKey?: string }).apiKey === 'good-key'
      return { valid, message: valid ? 'Accepted' : 'Key rejected' } as never
    }
    if (path === '/api/v1/settings/seerr' && method === 'GET')
      return { url: '', hasApiKey: false, userId: 1 } as never
    if (path === '/api/v1/settings/seerr' && method === 'PUT')
      return { url: 'http://service', hasApiKey: true } as never
    if (path === '/api/v1/settings/seerr/test-candidate') {
      if (!seerrValid)
        throw new ApiError(502, 'Seerr rejected the key in plain words')
      return { warnings: [] } as never
    }
    if (path === '/api/v1/settings/fulfilment.mode') return undefined as never
    if (path === '/api/v1/kid-profiles') return [kid()] as never
    if (path === '/api/v1/plex/users')
      return [{ id: '123', name: 'Demo' }] as never
    if (path === '/api/v1/setup/complete') {
      complete = true
      return undefined as never
    }
    throw new Error(`Unexpected request ${method} ${path}`)
  })
}

function puts(path: string) {
  return call.mock.calls.filter(
    ([method, called]) => method === 'PUT' && called === path,
  )
}

async function continueWhenReady(user: ReturnType<typeof userEvent.setup>) {
  const next = await screen.findByRole('button', { name: 'Continue' })
  await waitFor(() => expect(next).toHaveProperty('disabled', false))
  await user.click(next)
}

afterEach(() => {
  cleanup()
  call.mockReset()
  restricted = false
  seerrValid = false
  complete = false
  showGuide = true
  statusFails = false
})

it('tests a new TMDB key before saving it and never shows it again', async () => {
  install()
  const { default: Step } = await import('./setup/ConnectionSetupStep')
  const user = userEvent.setup()
  render(<Step service="tmdb" onNext={() => undefined} />)
  expect(await screen.findByText(/This product uses TMDB/)).toBeTruthy()
  const tmdbKey = await screen.findByLabelText('API key or read access token')
  await user.type(tmdbKey, 'wrong-key')
  await user.click(
    screen.getByRole('button', { name: 'Save and test connection' }),
  )
  expect(await screen.findByRole('alert')).toHaveProperty(
    'textContent',
    'Key rejected',
  )
  expect(puts('/api/v1/settings/tmdb.api_key')).toHaveLength(0)
  expect(tmdbKey).toHaveProperty('value', 'wrong-key')
  expect(screen.getByText(/Nothing was saved/)).toBeTruthy()
  expect(screen.getByRole('button', { name: 'Continue' })).toHaveProperty(
    'disabled',
    true,
  )
  await user.clear(tmdbKey)
  await user.type(tmdbKey, 'good-key')
  await user.click(
    screen.getByRole('button', { name: 'Save and test connection' }),
  )
  await waitFor(() =>
    expect(screen.getByRole('button', { name: 'Continue' })).toHaveProperty(
      'disabled',
      false,
    ),
  )
  expect(puts('/api/v1/settings/tmdb.api_key')).toEqual([
    [
      'PUT',
      '/api/v1/settings/tmdb.api_key',
      { value: 'good-key', isSecret: true },
    ],
  ])
  expect(tmdbKey).toHaveProperty('value', '')
  expect(document.body.textContent).not.toContain('good-key')
  expect(document.body.innerHTML).not.toContain('good-key')
})

it('keeps a failed Seerr value unsaved until the parent saves the same value again', async () => {
  install()
  const { default: Step } = await import('./setup/ConnectionSetupStep')
  const user = userEvent.setup()
  render(<Step service="seerr" onNext={() => undefined} />)
  await user.type(
    await screen.findByLabelText('Service address'),
    'http://service',
  )
  const seerrKey = screen.getByLabelText('API key or read access token')
  await user.type(seerrKey, 'another-key')
  await user.click(
    screen.getByRole('button', { name: 'Save and test connection' }),
  )
  expect((await screen.findByRole('alert')).textContent).toBe(
    'Seerr rejected the key in plain words',
  )
  expect(puts('/api/v1/settings/seerr')).toHaveLength(0)
  expect(seerrKey).toHaveProperty('value', 'another-key')
  await user.click(
    screen.getByRole('button', { name: 'Save and test connection' }),
  )
  expect(await screen.findByText(/Saved without a passing/)).toBeTruthy()
  expect(puts('/api/v1/settings/seerr')).toEqual([
    [
      'PUT',
      '/api/v1/settings/seerr',
      { url: 'http://service', apiKey: 'another-key', userId: 1 },
    ],
  ])
  expect(seerrKey).toHaveProperty('value', '')
  expect(document.body.textContent).not.toContain('another-key')
  expect(screen.getByRole('button', { name: 'Continue' })).toHaveProperty(
    'disabled',
    true,
  )
  seerrValid = true
  await user.click(
    screen.getByRole('button', { name: 'Save and test connection' }),
  )
  await waitFor(() =>
    expect(screen.getByRole('button', { name: 'Continue' })).toHaveProperty(
      'disabled',
      false,
    ),
  )
  expect(call).toHaveBeenCalledWith(
    'POST',
    '/api/v1/settings/seerr/test-candidate',
    { url: 'http://service', apiKey: undefined, userId: 1 },
  )
})

it('walks through each step, checks restrictions, finishes and can be rerun', async () => {
  seerrValid = true
  install()
  const user = userEvent.setup()
  render(
    <SetupGuide>
      <p>Parent settings</p>
    </SetupGuide>,
  )
  await screen.findByRole('heading', { name: 'Connect Plex' })
  expect(screen.queryByText('Parent settings')).toBeNull()
  expect(screen.getByRole('button', { name: 'Continue' })).toHaveProperty(
    'disabled',
    true,
  )
  await user.click(
    await screen.findByRole('button', { name: 'Test connection' }),
  )
  await continueWhenReady(user)
  await user.type(
    await screen.findByLabelText('API key or read access token'),
    'good-key',
  )
  await user.click(
    screen.getByRole('button', { name: 'Save and test connection' }),
  )
  await continueWhenReady(user)
  await user.type(
    await screen.findByLabelText('Service address'),
    'http://service',
  )
  await user.type(screen.getByLabelText('API key or read access token'), 'k')
  await user.click(
    screen.getByRole('button', { name: 'Save and test connection' }),
  )
  await continueWhenReady(user)

  await user.selectOptions(
    await screen.findByLabelText('Existing kid profile'),
    '1',
  )
  await user.click(screen.getByRole('button', { name: 'Continue' }))
  expect(
    await screen.findByText('Profile: Demo. Plex label: Demo.'),
  ).toBeTruthy()
  await user.click(
    await screen.findByRole('button', { name: 'Test Plex restrictions' }),
  )
  expect((await screen.findByRole('alert')).textContent).toContain(
    'could not be confirmed',
  )
  expect(screen.getByText('movies: restrictions not confirmed.')).toBeTruthy()
  expect(screen.getByRole('button', { name: 'Finish setup' })).toHaveProperty(
    'disabled',
    true,
  )
  restricted = true
  await user.click(
    screen.getByRole('button', { name: 'Test Plex restrictions' }),
  )
  await waitFor(() =>
    expect(screen.getByRole('button', { name: 'Finish setup' })).toHaveProperty(
      'disabled',
      false,
    ),
  )
  expect(screen.getByText('movies: restrictions confirmed.')).toBeTruthy()
  expect(screen.getByText('TV shows: restrictions confirmed.')).toBeTruthy()
  await user.click(screen.getByRole('button', { name: 'Finish setup' }))
  expect(await screen.findByText('Parent settings')).toBeTruthy()
  await user.click(
    screen.getByRole('button', { name: 'Run guided setup again' }),
  )
  expect(
    await screen.findByRole('heading', { name: 'Connect Plex' }),
  ).toBeTruthy()
  await user.click(screen.getByRole('button', { name: 'Exit guided setup' }))
  expect(screen.getByText('Parent settings')).toBeTruthy()
  expect(call).toHaveBeenCalledWith('POST', '/api/v1/setup/dismiss')
})

it('stays closed when the guide is not due and can still be opened from settings', async () => {
  showGuide = false
  install()
  const user = userEvent.setup()
  render(
    <SetupGuide>
      <p>Parent settings</p>
    </SetupGuide>,
  )
  expect(await screen.findByText('Parent settings')).toBeTruthy()
  expect(screen.queryByRole('heading', { name: 'Connect Plex' })).toBeNull()
  await user.click(
    screen.getByRole('button', { name: 'Run guided setup again' }),
  )
  expect(
    await screen.findByRole('heading', { name: 'Connect Plex' }),
  ).toBeTruthy()
})

it('keeps the other settings available when the setup status cannot be loaded', async () => {
  statusFails = true
  showGuide = false
  install()
  const user = userEvent.setup()
  render(
    <SetupGuide>
      <p>Parent settings</p>
    </SetupGuide>,
  )
  expect(await screen.findByText('Parent settings')).toBeTruthy()
  expect((await screen.findByRole('alert')).textContent).toContain(
    'Setup could not be loaded',
  )
  expect(
    screen.getByRole('button', { name: 'Run guided setup again' }),
  ).toBeTruthy()
  statusFails = false
  await user.click(screen.getByRole('button', { name: 'Try again' }))
  await waitFor(() => expect(screen.queryByRole('alert')).toBeNull())
  expect(screen.getByText('Parent settings')).toBeTruthy()
})

it('requires an available Arr quality profile and root folder before continuing', async () => {
  const { default: Step } = await import('./setup/ConnectionSetupStep')
  const validated = vi.fn()
  call.mockImplementation(async (method, path) => {
    if (path === '/api/v1/settings/sonarr')
      return { url: 'http://service', hasApiKey: true } as never
    if (path === '/api/v1/settings/sonarr/test-candidate')
      return { valid: true } as never
    if (path === '/api/v1/sonarr/profiles')
      return [{ id: 4, name: 'HD' }] as never
    if (path === '/api/v1/sonarr/rootfolders')
      return [{ path: '/media' }] as never
    throw new Error(`${method} ${path}`)
  })
  const user = userEvent.setup()
  render(<Step service="sonarr" onValidated={validated} />)
  await user.click(
    await screen.findByRole('button', { name: 'Save and test connection' }),
  )
  expect((await screen.findByRole('alert')).textContent).toContain(
    'Choose an available',
  )
  expect(validated).not.toHaveBeenCalledWith(true)
  await user.selectOptions(screen.getByLabelText('Quality profile'), '4')
  await user.selectOptions(screen.getByLabelText('Root folder'), '/media')
  await user.click(
    screen.getByRole('button', { name: 'Save and test connection' }),
  )
  await waitFor(() => expect(validated).toHaveBeenCalledWith(true))
  expect(call).toHaveBeenLastCalledWith('GET', '/api/v1/sonarr/rootfolders')
  expect(call).toHaveBeenCalledWith('PUT', '/api/v1/settings/sonarr', {
    url: 'http://service',
    apiKey: undefined,
    qualityProfileId: 4,
    rootFolderPath: '/media',
    tagIds: [],
  })
  await user.selectOptions(screen.getByLabelText('Root folder'), '')
  expect(validated).toHaveBeenLastCalledWith(false)
})

it('creates a profile only after selecting a Plex user', async () => {
  const { default: Step } = await import('./setup/KidSetupStep')
  const next = vi.fn()
  call.mockImplementation(async (method, path) => {
    if (path === '/api/v1/kid-profiles' && method === 'GET') return [] as never
    if (path === '/api/v1/plex/users')
      return [{ id: '123', name: 'Demo' }] as never
    if (path === '/api/v1/kid-profiles' && method === 'POST')
      return { id: 7 } as never
    throw new Error(path)
  })
  const user = userEvent.setup()
  render(<Step profileId={null} onNext={next} />)
  await user.type(await screen.findByLabelText('Name'), 'Demo')
  expect(
    screen.getByRole('button', { name: 'Save kid profile and continue' }),
  ).toHaveProperty('disabled', true)
  await user.selectOptions(await screen.findByLabelText('Plex user'), '123')
  await user.click(
    screen.getByRole('button', { name: 'Save kid profile and continue' }),
  )
  await waitFor(() => expect(next).toHaveBeenCalledWith(7))
  expect(call).toHaveBeenCalledWith(
    'POST',
    '/api/v1/kid-profiles',
    expect.objectContaining({
      name: 'Demo',
      plexUserId: '123',
      autoApprove: false,
    }),
  )
})
