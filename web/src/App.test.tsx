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
      calls.push({
        method,
        path,
        body: init?.body ? JSON.parse(init.body as string) : undefined,
      })
      const body = path.startsWith('/api/v1/requests')
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
  expect(screen.queryByRole('link', { name: 'About' })).toBeNull()
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
  expect(screen.getByRole('button', { name: 'Sign out' })).toBeTruthy()
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

it('has a friendly page for unknown routes', () => {
  stubFetch(() => null)
  renderAt('/nowhere')
  expect(
    screen.getByRole('heading', { name: 'That page does not exist.' }),
  ).toBeTruthy()
})
