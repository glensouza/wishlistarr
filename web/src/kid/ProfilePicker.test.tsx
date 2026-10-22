import { cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import App from '../App'
import { axe } from '../test/axe'

type Call = { method: string; path: string; body: unknown }

const profiles = [
  { id: 1, name: 'Ada', avatar: 'fox', requiresPin: true },
  { id: 2, name: 'Bo', avatar: 'owl', requiresPin: false },
]

let calls: Call[]
let signedInAs: string | null
let signInResponse: () => Response

beforeEach(() => {
  calls = []
  signedInAs = null
  signInResponse = () => Response.json(profiles[0])
  vi.stubGlobal(
    'fetch',
    vi.fn(async (path: string, init?: RequestInit) => {
      const method = init?.method ?? 'GET'
      if (path === '/api/v1/auth/antiforgery') {
        return Response.json({ token: 'tok', headerName: 'X-CSRF-TOKEN' })
      }
      if (path === '/api/v1/auth/status') {
        return Response.json({
          setupRequired: false,
          principalType: signedInAs ? 'Kid' : null,
          kidDevice: { name: 'Tablet' },
        })
      }
      calls.push({
        method,
        path,
        body: init?.body ? JSON.parse(init.body as string) : undefined,
      })
      if (path === '/api/v1/kid/profiles') return Response.json(profiles)
      if (path === '/api/v1/kid/me') {
        return signedInAs
          ? Response.json({ id: '1', name: signedInAs })
          : Response.json({ title: 'Unauthorized' }, { status: 401 })
      }
      if (path === '/api/v1/kid/signin') {
        const response = signInResponse()
        if (response.ok) {
          signedInAs = (await response.clone().json()).name
        }
        return response
      }
      if (path === '/api/v1/kid/signout') {
        signedInAs = null
        return new Response(null, { status: 204 })
      }
      return Response.json({}, { status: 404 })
    }),
  )
})

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

const renderApp = () =>
  render(
    <MemoryRouter>
      <App />
    </MemoryRouter>,
  )

const tap = async (user: ReturnType<typeof userEvent.setup>, pin: string) => {
  for (const digit of pin) {
    await user.click(screen.getByRole('button', { name: digit }))
  }
}

it('lists the profiles with avatars and has no accessibility violations', async () => {
  const { container } = renderApp()
  expect(
    await screen.findByRole('button', { name: 'Sign in as Ada' }),
  ).toBeTruthy()
  expect(screen.getByRole('button', { name: 'Sign in as Bo' })).toBeTruthy()
  expect(container.querySelectorAll('.avatar')).toHaveLength(2)
  expect(await axe(container)).toHaveNoViolations()
})

it('shows an accessible PIN pad for a profile with a PIN', async () => {
  const user = userEvent.setup()
  const { container } = renderApp()
  await user.click(
    await screen.findByRole('button', { name: 'Sign in as Ada' }),
  )
  expect(
    screen.getByRole('heading', { name: 'Hi Ada! Enter your PIN' }),
  ).toBeTruthy()
  expect(screen.getByRole('button', { name: 'Go' })).toHaveProperty(
    'disabled',
    true,
  )
  expect(await axe(container)).toHaveNoViolations()
})

it('signs in with the entered PIN and lands on the kid home', async () => {
  const user = userEvent.setup()
  renderApp()
  await user.click(
    await screen.findByRole('button', { name: 'Sign in as Ada' }),
  )
  await tap(user, '1234')
  expect(screen.getByRole('status').textContent).toBe(
    'PIN entered: 4 of 8 digits',
  )
  await user.click(screen.getByRole('button', { name: 'Go' }))
  expect(
    await screen.findByRole('heading', { name: 'Welcome, Ada!' }),
  ).toBeTruthy()
  const signIn = calls.find((call) => call.path === '/api/v1/kid/signin')
  expect(signIn?.body).toEqual({ profileId: 1, pin: '1234' })
})

it('accepts the PIN from the keyboard', async () => {
  const user = userEvent.setup()
  renderApp()
  await user.click(
    await screen.findByRole('button', { name: 'Sign in as Ada' }),
  )
  await user.keyboard('5678{Backspace}9{Enter}')
  await screen.findByRole('heading', { name: 'Welcome, Ada!' })
  const signIn = calls.find((call) => call.path === '/api/v1/kid/signin')
  expect(signIn?.body).toEqual({ profileId: 1, pin: '5679' })
})

it('signs in directly when the profile has no PIN', async () => {
  const user = userEvent.setup()
  signInResponse = () => Response.json(profiles[1])
  renderApp()
  await user.click(await screen.findByRole('button', { name: 'Sign in as Bo' }))
  expect(
    await screen.findByRole('heading', { name: 'Welcome, Bo!' }),
  ).toBeTruthy()
  const signIn = calls.find((call) => call.path === '/api/v1/kid/signin')
  expect(signIn?.body).toEqual({ profileId: 2 })
})

it('explains a wrong PIN and lets the kid try again', async () => {
  const user = userEvent.setup()
  signInResponse = () =>
    Response.json({ title: 'Invalid PIN.' }, { status: 401 })
  renderApp()
  await user.click(
    await screen.findByRole('button', { name: 'Sign in as Ada' }),
  )
  await tap(user, '0000')
  await user.click(screen.getByRole('button', { name: 'Go' }))
  expect((await screen.findByRole('alert')).textContent).toBe(
    'That PIN did not work. Try again.',
  )
  expect(screen.getByRole('button', { name: 'Go' })).toBeTruthy()
})

it('tells a locked profile to ask a parent', async () => {
  const user = userEvent.setup()
  signInResponse = () => Response.json({ title: 'Locked' }, { status: 423 })
  renderApp()
  await user.click(
    await screen.findByRole('button', { name: 'Sign in as Ada' }),
  )
  await tap(user, '1234')
  await user.click(screen.getByRole('button', { name: 'Go' }))
  expect((await screen.findByRole('alert')).textContent).toBe(
    'This profile is locked. Ask a parent to unlock it.',
  )
})

it('shows how long to wait when backed off', async () => {
  const user = userEvent.setup()
  signInResponse = () =>
    Response.json(
      { title: 'Too many' },
      { status: 429, headers: { 'Retry-After': '8' } },
    )
  renderApp()
  await user.click(
    await screen.findByRole('button', { name: 'Sign in as Ada' }),
  )
  await tap(user, '1234')
  await user.click(screen.getByRole('button', { name: 'Go' }))
  expect((await screen.findByRole('alert')).textContent).toBe(
    'Too many tries. Wait 8 seconds and try again.',
  )
})

it('goes back to the picker with Not me', async () => {
  const user = userEvent.setup()
  renderApp()
  await user.click(
    await screen.findByRole('button', { name: 'Sign in as Ada' }),
  )
  await user.click(screen.getByRole('button', { name: 'Not me' }))
  expect(
    await screen.findByRole('heading', { name: 'Who is watching?' }),
  ).toBeTruthy()
})

it('signs out back to the picker', async () => {
  const user = userEvent.setup()
  signedInAs = 'Ada'
  render(
    <MemoryRouter initialEntries={['/home']}>
      <App />
    </MemoryRouter>,
  )
  await user.click(
    await screen.findByRole('button', { name: 'Switch profile' }),
  )
  await waitFor(() =>
    expect(
      screen.getByRole('heading', { name: 'Who is watching?' }),
    ).toBeTruthy(),
  )
})

it('offers a retry when the profiles cannot be loaded', async () => {
  const user = userEvent.setup()
  const original = fetch
  let fail = true
  vi.stubGlobal(
    'fetch',
    vi.fn(async (path: string, init?: RequestInit) =>
      fail && path === '/api/v1/kid/profiles'
        ? Response.json({ title: 'down' }, { status: 500 })
        : original(path, init),
    ),
  )
  renderApp()
  expect(
    await screen.findByText('We could not load the profiles.'),
  ).toBeTruthy()
  fail = false
  await user.click(screen.getByRole('button', { name: 'Try again' }))
  expect(
    await screen.findByRole('button', { name: 'Sign in as Ada' }),
  ).toBeTruthy()
})
