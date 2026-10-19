import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import App from './App'

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

it('renders the app title', () => {
  vi.stubGlobal(
    'fetch',
    vi.fn(() => new Promise(() => {})),
  )
  render(<App />)
  expect(screen.getByRole('heading', { name: 'Wishlistarr' })).toBeTruthy()
})

it('asks for the parent sign-in when nobody is signed in', async () => {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () =>
      Response.json({ setupRequired: false, principalType: null }),
    ),
  )
  render(<App />)
  expect(await screen.findByRole('button', { name: 'Sign in' })).toBeTruthy()
})

it('shows the kid profiles to a signed-in parent', async () => {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (path: string) =>
      Response.json(
        path.endsWith('/auth/status')
          ? { setupRequired: false, principalType: 'Parent' }
          : [],
      ),
    ),
  )
  render(<App />)
  expect(
    await screen.findByRole('heading', { name: 'Kid profiles' }),
  ).toBeTruthy()
})
