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

const paired = {
  setupRequired: false,
  principalType: null,
  kidDevice: { name: 'Tablet' },
}
const unpaired = { setupRequired: false, principalType: null, kidDevice: null }

function stubFetch(handler: (path: string, method: string) => unknown) {
  const calls: Call[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (path: string, init?: RequestInit) => {
      const method = init?.method ?? 'GET'
      if (path === '/api/v1/auth/antiforgery') {
        return Response.json({ token: 'tok', headerName: 'X-CSRF-TOKEN' })
      }
      if (path === '/api/v1/backups' && method === 'GET') {
        return Response.json({
          retention: 7,
          restorePending: false,
          backups: [],
        })
      }
      calls.push({
        method,
        path,
        body: init?.body ? JSON.parse(init.body as string) : undefined,
      })
      const body =
        path === '/api/v1/updates'
          ? { checkEnabled: true, available: null }
          : path.startsWith('/api/v1/requests')
            ? { items: [], total: 0, limit: 100, offset: 0 }
            : handler(path, method)
      if (body === undefined) return new Response(null, { status: 204 })
      return body === null
        ? Response.json({ title: 'Nope' }, { status: 401 })
        : Response.json(body)
    }),
  )
  return calls
}

it('shows the shell with a skip link and landmarks', async () => {
  stubFetch((path) =>
    path === '/api/v1/auth/status'
      ? paired
      : path === '/api/v1/kid/profiles'
        ? []
        : null,
  )
  const { container } = renderAt('/')
  expect(screen.getByRole('link', { name: 'Skip to content' })).toBeTruthy()
  expect(screen.getByRole('banner')).toBeTruthy()
  expect(screen.getByRole('main')).toBeTruthy()
  expect(screen.getByRole('link', { name: 'Wishlistarr' })).toBeTruthy()
  expect(screen.getByRole('contentinfo')).toBeTruthy()
  expect(screen.queryByRole('link', { name: 'About' })).toBeNull()
  await screen.findByText('No profiles yet. Ask a parent to add one.')
  expect(await axe(container)).toHaveNoViolations()
})

it('sends an unpaired browser to the parent sign-in without loading the picker', async () => {
  const calls = stubFetch((path) =>
    path === '/api/v1/auth/status' ? unpaired : null,
  )
  const { container } = renderAt('/')
  expect(await screen.findByRole('button', { name: 'Sign in' })).toBeTruthy()
  expect(
    screen.getByRole('heading', { name: 'Use this device for kids' }),
  ).toBeTruthy()
  expect(screen.queryByRole('heading', { name: 'Who is watching?' })).toBeNull()
  expect(calls.some((call) => call.path === '/api/v1/kid/profiles')).toBe(false)
  expect(await axe(container)).toHaveNoViolations()
})

it('pairs the browser with a code and then shows the picker', async () => {
  const user = userEvent.setup()
  let isPaired = false
  const calls = stubFetch((path) => {
    if (path === '/api/v1/auth/status') return isPaired ? paired : unpaired
    if (path === '/api/v1/kid/pair') {
      isPaired = true
      return { name: 'Tablet' }
    }
    return path === '/api/v1/kid/profiles' ? [] : null
  })
  renderAt('/parent')
  await user.type(await screen.findByLabelText('Pairing code'), 'abcd-2345')
  await user.click(screen.getByRole('button', { name: 'Pair this device' }))
  expect(
    await screen.findByRole('heading', { name: 'Who is watching?' }),
  ).toBeTruthy()
  expect(calls.find((call) => call.path === '/api/v1/kid/pair')?.body).toEqual({
    code: 'abcd-2345',
  })
})

it('explains a pairing code that did not work', async () => {
  const user = userEvent.setup()
  vi.stubGlobal(
    'fetch',
    vi.fn(async (path: string) =>
      path === '/api/v1/auth/status'
        ? Response.json(unpaired)
        : Response.json({ title: 'Bad code' }, { status: 400 }),
    ),
  )
  renderAt('/parent')
  await user.type(await screen.findByLabelText('Pairing code'), 'zzzz-zzzz')
  await user.click(screen.getByRole('button', { name: 'Pair this device' }))
  expect((await screen.findByRole('alert')).textContent).toBe(
    'That code did not work. It may have expired or already been used.',
  )
})

it('asks for the parent sign-in when nobody is signed in', async () => {
  stubFetch(() => ({ setupRequired: false, principalType: null }))
  const { container } = renderAt('/parent')
  expect(await screen.findByRole('button', { name: 'Sign in' })).toBeTruthy()
  expect(
    screen.queryByRole('checkbox', { name: 'Let kids play YouTube trailers' }),
  ).toBeNull()
  expect(screen.queryByRole('link', { name: 'About' })).toBeNull()
  expect(
    screen.queryByRole('button', { name: 'Manage notifications' }),
  ).toBeNull()
  expect(await axe(container)).toHaveNoViolations()
})

it('needs the parent password to leave kid mode on a paired device', async () => {
  const user = userEvent.setup()
  let parentSignedIn = false
  const calls = stubFetch((path) => {
    if (path === '/api/v1/auth/status') {
      return parentSignedIn ? { ...paired, principalType: 'Parent' } : paired
    }
    if (path === '/api/v1/kid/exit') {
      parentSignedIn = true
      return undefined
    }
    return []
  })
  const { container } = renderAt('/parent')
  expect(
    await screen.findByRole('heading', { name: 'Leave kid mode' }),
  ).toBeTruthy()
  expect(screen.queryByRole('heading', { name: 'Kid profiles' })).toBeNull()
  expect(
    screen.queryByRole('button', { name: 'Manage notifications' }),
  ).toBeNull()
  expect(await axe(container)).toHaveNoViolations()

  await user.type(screen.getByLabelText('Username'), 'parent')
  await user.type(screen.getByLabelText('Password'), 'secret words')
  await user.click(screen.getByRole('button', { name: 'Leave kid mode' }))

  expect(
    await screen.findByRole('heading', { name: 'Kid profiles' }),
  ).toBeTruthy()
  expect(
    screen.getByRole('button', { name: 'Return to kid mode' }),
  ).toBeTruthy()
  expect(calls.find((call) => call.path === '/api/v1/kid/exit')?.body).toEqual({
    username: 'parent',
    password: 'secret words',
  })
})

it('keeps kid mode when the parent password is wrong', async () => {
  const user = userEvent.setup()
  vi.stubGlobal(
    'fetch',
    vi.fn(async (path: string) => {
      if (path === '/api/v1/auth/status') return Response.json(paired)
      if (path === '/api/v1/auth/antiforgery') {
        return Response.json({ token: 'tok', headerName: 'X-CSRF-TOKEN' })
      }
      return Response.json(
        { title: 'Invalid username or password.' },
        { status: 401 },
      )
    }),
  )
  renderAt('/parent')
  await user.type(await screen.findByLabelText('Username'), 'parent')
  await user.type(screen.getByLabelText('Password'), 'wrong')
  await user.click(screen.getByRole('button', { name: 'Leave kid mode' }))
  expect((await screen.findByRole('alert')).textContent).toBe(
    'Invalid username or password.',
  )
  expect(screen.queryByRole('heading', { name: 'Kid profiles' })).toBeNull()
})

it('returns a paired device to kid mode when the parent signs out', async () => {
  const user = userEvent.setup()
  let parentSignedIn = true
  const calls = stubFetch((path) => {
    if (path === '/api/v1/auth/status') {
      return parentSignedIn ? { ...paired, principalType: 'Parent' } : paired
    }
    if (path === '/api/v1/auth/logout') {
      parentSignedIn = false
      return undefined
    }
    return []
  })
  renderAt('/parent')
  await user.click(
    await screen.findByRole('button', { name: 'Return to kid mode' }),
  )
  expect(
    await screen.findByRole('heading', { name: 'Who is watching?' }),
  ).toBeTruthy()
  expect(calls.some((call) => call.path === '/api/v1/auth/logout')).toBe(true)
})

it('shows the kid profiles and devices to a signed-in parent', async () => {
  stubFetch((path) =>
    path.endsWith('/auth/status')
      ? { setupRequired: false, principalType: 'Parent', kidDevice: null }
      : [],
  )
  renderAt('/parent')
  expect(
    await screen.findByRole('heading', { name: 'Kid profiles' }),
  ).toBeTruthy()
  expect(screen.getByRole('heading', { name: 'Kid devices' })).toBeTruthy()
  expect(
    screen.getByRole('button', { name: 'Manage notifications' }),
  ).toBeTruthy()
  expect(screen.getByRole('button', { name: 'Sign out' })).toBeTruthy()
  const trailers = (await screen.findByRole('checkbox', {
    name: 'Let kids play YouTube trailers',
  })) as HTMLInputElement
  expect(trailers.checked).toBe(false)
})

it('sends signed-out visitors from the kid home back to the picker', async () => {
  stubFetch((path) =>
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
})

it('does not show the kid home on an unpaired browser', async () => {
  stubFetch((path) => (path === '/api/v1/auth/status' ? unpaired : null))
  renderAt('/home')
  await waitFor(() =>
    expect(screen.getByRole('button', { name: 'Sign in' })).toBeTruthy(),
  )
})

const parent = {
  setupRequired: false,
  principalType: 'Parent',
  kidDevice: null,
}

it('shows a banner when a health check fails', async () => {
  stubFetch((path) => {
    if (path === '/api/v1/auth/status') return parent
    if (path === '/api/v1/system/health') {
      return [
        {
          integration: 'plex',
          status: 'error',
          message: 'Plex refused the sign-in.',
          fix: 'Link Plex again.',
        },
        {
          integration: 'custom-box',
          status: 'ok',
          message: 'The custom box is quiet.',
          fix: 'Leave it.',
        },
      ]
    }
    return []
  })
  const { container } = renderAt('/parent')
  const alert = await screen.findByRole('alert')
  expect(alert.textContent).toContain('Plex refused the sign-in.')
  expect(alert.textContent).toContain('Link Plex again.')
  expect(alert.textContent).not.toContain('The custom box is quiet.')
  expect(screen.getByText('custom-box')).toBeTruthy()
  expect(screen.getAllByText('Plex')).toHaveLength(2)
  expect(await axe(container)).toHaveNoViolations()
})

it('says health checks passed when every check is ok', async () => {
  stubFetch((path) =>
    path === '/api/v1/auth/status'
      ? parent
      : path === '/api/v1/system/health'
        ? [
            {
              integration: 'database',
              status: 'ok',
              message: 'The database is reachable.',
              fix: 'Keep the data directory writable.',
            },
          ]
        : [],
  )
  renderAt('/parent')
  expect(await screen.findByText('All checks passed.')).toBeTruthy()
  expect(screen.queryByRole('alert')).toBeNull()
})

it('fails closed when health checks are not a list', async () => {
  stubFetch((path) =>
    path === '/api/v1/auth/status'
      ? parent
      : path === '/api/v1/system/health'
        ? { status: 'ok' }
        : [],
  )
  renderAt('/parent')
  expect((await screen.findByRole('alert')).textContent).toContain(
    'Health checks could not be loaded.',
  )
  expect(screen.queryByText('All checks passed.')).toBeNull()
})

it('does not load health checks on the kid screens', async () => {
  const calls = stubFetch((path) =>
    path === '/api/v1/auth/status'
      ? paired
      : path === '/api/v1/kid/profiles'
        ? []
        : null,
  )
  renderAt('/')
  await screen.findByText('No profiles yet. Ask a parent to add one.')
  expect(calls.some((call) => call.path.includes('/system/health'))).toBe(false)
  expect(calls.some((call) => call.path.includes('/system/logs'))).toBe(false)
  expect(screen.queryByRole('heading', { name: 'Health' })).toBeNull()
})

it('filters logs and offers a download', async () => {
  const user = userEvent.setup()
  const calls = stubFetch((path) => {
    if (path === '/api/v1/auth/status') return parent
    if (path.startsWith('/api/v1/system/logs')) {
      return path.includes('level=warning')
        ? [
            {
              at: '2026-01-02T00:00:00.000Z',
              level: 'warning',
              category: 'Wishlistarr.Test',
              message: 'disk is full',
            },
          ]
        : []
    }
    return []
  })
  renderAt('/parent')
  expect(await screen.findByText('No log entries.')).toBeTruthy()
  expect(
    screen.getByRole('link', { name: 'Download' }).getAttribute('href'),
  ).toBe('/api/v1/system/logs/download?level=information')
  await user.selectOptions(screen.getByLabelText('Level'), 'warning')
  expect(await screen.findByText('disk is full')).toBeTruthy()
  expect(
    screen.getByRole('link', { name: 'Download' }).getAttribute('href'),
  ).toBe('/api/v1/system/logs/download?level=warning')
  expect(calls.some((call) => call.path.includes('level=warning'))).toBe(true)
})

it('shows request history for a profile', async () => {
  stubFetch((path) => {
    if (path === '/api/v1/auth/status') return parent
    if (path === '/api/v1/kid-profiles') {
      return [
        {
          id: 4,
          name: 'Ada',
          avatar: 'fox',
          isLocked: false,
          plexRestrictions: null,
        },
      ]
    }
    if (path === '/api/v1/kid-profiles/4/events') {
      return [
        {
          id: 8,
          requestId: 3,
          title: 'Odd Title',
          fromState: null,
          toState: 'mystery',
          actorName: 'system',
          reason: null,
          occurredAt: '2026-03-01T00:00:00.000Z',
        },
        {
          id: 7,
          requestId: 2,
          title: 'The Matrix',
          fromState: 'pending',
          toState: 'denied',
          actorName: 'parent',
          reason: 'Not tonight',
          occurredAt: '2026-01-02T15:04:00.000Z',
        },
      ]
    }
    return []
  })
  renderAt('/parent')
  const list = await screen.findByRole('list', { name: 'Ada' })
  expect(list.textContent).toContain('Odd Title')
  expect(list.textContent).toContain('New → mystery')
  expect(list.textContent).toContain('The Matrix')
  expect(list.textContent).toContain('Pending → Denied')
  expect(list.textContent).toContain('Not tonight')
  expect(list.textContent?.indexOf('Odd Title')).toBeLessThan(
    list.textContent?.indexOf('The Matrix') ?? 0,
  )
})

it('has a friendly page for unknown routes', () => {
  stubFetch(() => null)
  renderAt('/nowhere')
  expect(
    screen.getByRole('heading', { name: 'That page does not exist.' }),
  ).toBeTruthy()
})
