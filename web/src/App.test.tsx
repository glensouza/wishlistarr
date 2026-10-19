import { cleanup, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, expect, it, vi } from 'vitest'
import App from './App'
import { axe } from './test/axe'

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

function stubFetch(handler: (path: string) => unknown) {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (path: string) => {
      const body = handler(path)
      return body === null
        ? Response.json({ title: 'Nope' }, { status: 401 })
        : Response.json(body)
    }),
  )
}

it('shows the shell with a skip link and landmarks', async () => {
  stubFetch((path) => (path === '/api/v1/kid/profiles' ? [] : null))
  const { container } = renderAt('/')
  expect(screen.getByRole('link', { name: 'Skip to content' })).toBeTruthy()
  expect(screen.getByRole('banner')).toBeTruthy()
  expect(screen.getByRole('main')).toBeTruthy()
  expect(screen.getByRole('link', { name: 'Wishlistarr' })).toBeTruthy()
  await screen.findByText('No profiles yet. Ask a parent to add one.')
  expect(await axe(container)).toHaveNoViolations()
})

it('asks for the parent sign-in when nobody is signed in', async () => {
  stubFetch(() => ({ setupRequired: false, principalType: null }))
  const { container } = renderAt('/parent')
  expect(await screen.findByRole('button', { name: 'Sign in' })).toBeTruthy()
  expect(await axe(container)).toHaveNoViolations()
})

it('shows the kid profiles to a signed-in parent', async () => {
  stubFetch((path) =>
    path.endsWith('/auth/status')
      ? { setupRequired: false, principalType: 'Parent' }
      : [],
  )
  renderAt('/parent')
  expect(
    await screen.findByRole('heading', { name: 'Kid profiles' }),
  ).toBeTruthy()
})

it('sends signed-out visitors from the kid home back to the picker', async () => {
  stubFetch((path) => (path === '/api/v1/kid/profiles' ? [] : null))
  renderAt('/home')
  expect(
    await screen.findByRole('heading', { name: 'Who is watching?' }),
  ).toBeTruthy()
})

it('has a friendly page for unknown routes', () => {
  stubFetch(() => null)
  renderAt('/nowhere')
  expect(
    screen.getByRole('heading', { name: 'That page does not exist.' }),
  ).toBeTruthy()
})
