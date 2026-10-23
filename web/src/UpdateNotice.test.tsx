import { cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, expect, it, vi } from 'vitest'
import App from './App'
import { axe } from './test/axe'

type Call = { method: string; path: string; body: unknown }

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

function renderAt(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <App />
    </MemoryRouter>,
  )
}

const parent = {
  setupRequired: false,
  principalType: 'Parent',
  kidDevice: null,
}
const paired = {
  setupRequired: false,
  principalType: null,
  kidDevice: { name: 'Tablet' },
}
const unpaired = { setupRequired: false, principalType: null, kidDevice: null }

const releaseNotesUrl =
  'https://github.com/glensouza/wishlistarr/releases/tag/v9.9.9'
const upgradeNotesUrl =
  'https://github.com/glensouza/wishlistarr/blob/main/docs/upgrading.md'

const notice = {
  checkEnabled: true,
  available: {
    version: '9.9.9',
    releaseNotesUrl,
    upgradeNotesUrl,
  },
}

function stubFetch(handler: (path: string, method: string) => unknown) {
  const calls: Call[] = []
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
      const body = handler(path, method)
      if (body === undefined) return new Response(null, { status: 204 })
      return body === null
        ? Response.json({ title: 'Nope' }, { status: 401 })
        : Response.json(body)
    }),
  )
  return calls
}

it('shows a parent the release notes and the upgrade notes', async () => {
  stubFetch((path) =>
    path === '/api/v1/auth/status'
      ? parent
      : path === '/api/v1/updates'
        ? notice
        : [],
  )
  const { container } = renderAt('/parent')
  expect(await screen.findByText('Version 9.9.9 is available.')).toBeTruthy()
  expect(screen.getByRole('heading', { name: 'Updates' })).toBeTruthy()
  expect(
    screen.getByRole('link', { name: 'Release notes' }).getAttribute('href'),
  ).toBe(releaseNotesUrl)
  expect(
    screen.getByRole('link', { name: 'Upgrade notes' }).getAttribute('href'),
  ).toBe(upgradeNotesUrl)
  expect(
    (
      (await screen.findByRole('checkbox', {
        name: 'Check for new versions',
      })) as HTMLInputElement
    ).checked,
  ).toBe(true)
  expect(await axe(container)).toHaveNoViolations()
})

it('turns the check off and loads the notice again', async () => {
  const user = userEvent.setup()
  let enabled = true
  const calls = stubFetch((path, method) => {
    if (path === '/api/v1/auth/status') return parent
    if (path === '/api/v1/updates' && method === 'PUT') {
      enabled = false
      return undefined
    }
    if (path === '/api/v1/updates') {
      return {
        checkEnabled: enabled,
        available: enabled ? notice.available : null,
      }
    }
    return []
  })
  renderAt('/parent')
  const checkbox = await screen.findByRole('checkbox', {
    name: 'Check for new versions',
  })
  await waitFor(() => expect((checkbox as HTMLInputElement).checked).toBe(true))
  await user.click(checkbox)
  await waitFor(() =>
    expect((checkbox as HTMLInputElement).checked).toBe(false),
  )
  expect(calls.find((call) => call.method === 'PUT')?.body).toEqual({
    checkEnabled: false,
  })
  expect(screen.queryByRole('link', { name: 'Release notes' })).toBeNull()
})

it('keeps the server message off the page when saving fails', async () => {
  const user = userEvent.setup()
  vi.stubGlobal(
    'fetch',
    vi.fn(async (path: string, init?: RequestInit) => {
      if (path === '/api/v1/auth/antiforgery') {
        return Response.json({ token: 'tok', headerName: 'X-CSRF-TOKEN' })
      }
      if (path === '/api/v1/auth/status') return Response.json(parent)
      if (path === '/api/v1/updates' && init?.method === 'PUT') {
        return Response.json(
          { title: 'The update check must be true or false.' },
          { status: 400 },
        )
      }
      if (path === '/api/v1/updates') return Response.json(notice)
      return Response.json([])
    }),
  )
  renderAt('/parent')
  await user.click(
    await screen.findByRole('checkbox', { name: 'Check for new versions' }),
  )
  expect((await screen.findByRole('alert')).textContent).toBe(
    'The update check could not be saved.',
  )
  expect(
    screen.queryByText('The update check must be true or false.'),
  ).toBeNull()
})

it('does not show a notice whose links leave GitHub', async () => {
  stubFetch((path) =>
    path === '/api/v1/auth/status'
      ? parent
      : path === '/api/v1/updates'
        ? {
            checkEnabled: true,
            available: {
              version: '9.9.9',
              releaseNotesUrl: 'https://evil.example/notes',
              upgradeNotesUrl,
            },
          }
        : [],
  )
  const { container } = renderAt('/parent')
  expect(
    await screen.findByRole('checkbox', { name: 'Check for new versions' }),
  ).toBeTruthy()
  expect(screen.queryByRole('link', { name: 'Release notes' })).toBeNull()
  expect(screen.queryByRole('link', { name: 'Upgrade notes' })).toBeNull()
  expect(container.textContent).not.toContain('evil.example')
})

it('does not show links when the upgrade notes address is wrong', async () => {
  stubFetch((path) =>
    path === '/api/v1/auth/status'
      ? parent
      : path === '/api/v1/updates'
        ? {
            checkEnabled: true,
            available: {
              version: '9.9.9',
              releaseNotesUrl,
              upgradeNotesUrl:
                'https://user:pass@github.com/glensouza/wishlistarr/blob/main/docs/upgrading.md',
            },
          }
        : [],
  )
  const { container } = renderAt('/parent')
  await screen.findByRole('checkbox', { name: 'Check for new versions' })
  expect(screen.queryByRole('link', { name: 'Release notes' })).toBeNull()
  expect(container.textContent).not.toContain('user:pass')
})

it('hides a newer version when the check is off', async () => {
  stubFetch((path) =>
    path === '/api/v1/auth/status'
      ? parent
      : path === '/api/v1/updates'
        ? { checkEnabled: false, available: notice.available }
        : [],
  )
  renderAt('/parent')
  const checkbox = await screen.findByRole('checkbox', {
    name: 'Check for new versions',
  })
  expect((checkbox as HTMLInputElement).checked).toBe(false)
  expect(screen.queryByRole('link', { name: 'Release notes' })).toBeNull()
  expect(screen.queryByText('Version 9.9.9 is available.')).toBeNull()
})

it('stays quiet when the update response is missing', async () => {
  stubFetch((path) => (path === '/api/v1/auth/status' ? parent : []))
  renderAt('/parent')
  expect(
    await screen.findByRole('heading', { name: 'Kid profiles' }),
  ).toBeTruthy()
  expect(
    screen.getByRole('checkbox', { name: 'Check for new versions' }),
  ).toBeTruthy()
  expect(screen.queryByRole('link', { name: 'Release notes' })).toBeNull()
})

it('does not ask for updates on kid screens', async () => {
  const calls = stubFetch((path) =>
    path === '/api/v1/auth/status'
      ? paired
      : path === '/api/v1/kid/profiles'
        ? []
        : null,
  )
  renderAt('/')
  expect(
    await screen.findByText('No profiles yet. Ask a parent to add one.'),
  ).toBeTruthy()
  expect(calls.some((call) => call.path === '/api/v1/updates')).toBe(false)
  expect(screen.queryByText('Check for new versions')).toBeNull()
  expect(screen.queryByRole('link', { name: 'Upgrade notes' })).toBeNull()

  cleanup()
  const home = stubFetch((path) =>
    path === '/api/v1/auth/status'
      ? paired
      : path === '/api/v1/kid/profiles'
        ? []
        : null,
  )
  renderAt('/home')
  expect(
    await screen.findByRole('heading', { name: 'Who is watching?' }),
  ).toBeTruthy()
  expect(home.some((call) => call.path === '/api/v1/updates')).toBe(false)
  expect(screen.queryByText('Version 9.9.9 is available.')).toBeNull()
})

it('does not show the notice before a parent is signed in', async () => {
  const calls = stubFetch((path) =>
    path === '/api/v1/auth/status' ? unpaired : null,
  )
  renderAt('/parent')
  expect(await screen.findByRole('button', { name: 'Sign in' })).toBeTruthy()
  expect(calls.some((call) => call.path === '/api/v1/updates')).toBe(false)
  expect(screen.queryByRole('heading', { name: 'Updates' })).toBeNull()

  cleanup()
  const pairedCalls = stubFetch((path) =>
    path === '/api/v1/auth/status' ? paired : null,
  )
  renderAt('/parent')
  expect(
    await screen.findByRole('heading', { name: 'Leave kid mode' }),
  ).toBeTruthy()
  expect(pairedCalls.some((call) => call.path === '/api/v1/updates')).toBe(
    false,
  )
  expect(screen.queryByText('Check for new versions')).toBeNull()
})
