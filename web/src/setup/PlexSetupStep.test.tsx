import { cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, expect, it, vi } from 'vitest'
import PlexSetupStep from './PlexSetupStep'
import { request } from '../api'

vi.mock('../api', () => ({
  request: vi.fn(),
  ApiError: class extends Error {
    status = 400
  },
}))
const call = vi.mocked(request)
const approvalUrl = 'https://app.plex.tv/auth#?clientID=fixture&code=fixture'
afterEach(() => {
  cleanup()
  call.mockReset()
})

it('links Plex, handles pending and expired approval, retries discovery, and requires a successful connection test', async () => {
  let state = 'pending'
  let discoveryWorks = false
  let valid = false
  let linkId = 122
  const next = vi.fn()
  call.mockImplementation(async (method, path) => {
    if (path === '/api/v1/plex/status')
      return { linked: false, server: null } as never
    if (path === '/api/v1/plex/link')
      return {
        id: String(++linkId),
        code: 'fixture',
        authUrl: approvalUrl,
        expiresAt: '2030-01-01T00:00:00Z',
      } as never
    if (path === `/api/v1/plex/link/${linkId}/check`) return { state } as never
    if (path === '/api/v1/plex/servers') {
      if (!discoveryWorks) throw new Error('offline')
      return [{ id: 'server', name: 'Demo server' }] as never
    }
    if (path === '/api/v1/plex/server') return undefined as never
    if (path === '/api/v1/settings/plex/test')
      return {
        valid,
        message: valid ? 'Connected' : 'Server is unavailable',
      } as never
    throw new Error(`${method} ${path}`)
  })
  const user = userEvent.setup()
  render(<PlexSetupStep onNext={next} />)
  const link = await screen.findByRole('button', { name: 'Link Plex account' })
  await waitFor(() => expect(link).toHaveProperty('disabled', false))
  await user.click(link)
  expect(await screen.findByText(approvalUrl)).toBeTruthy()
  await user.click(screen.getByRole('button', { name: 'Check Plex sign-in' }))
  expect((await screen.findByRole('alert')).textContent).toContain(
    'not approved',
  )
  state = 'expired'
  await user.click(screen.getByRole('button', { name: 'Check Plex sign-in' }))
  expect((await screen.findByRole('alert')).textContent).toContain('expired')
  await user.click(link)
  state = 'linked'
  await user.click(
    await screen.findByRole('button', { name: 'Check Plex sign-in' }),
  )
  expect((await screen.findByRole('alert')).textContent).toContain(
    'could not be connected',
  )
  expect(screen.getByRole('button', { name: 'Continue' })).toHaveProperty(
    'disabled',
    true,
  )
  discoveryWorks = true
  await user.click(
    screen.getByRole('button', { name: 'Find Plex servers again' }),
  )
  await user.selectOptions(
    await screen.findByLabelText('Plex server'),
    'server',
  )
  await user.click(screen.getByRole('button', { name: 'Test connection' }))
  expect((await screen.findByRole('alert')).textContent).toContain(
    'Server is unavailable',
  )
  expect(screen.getByRole('button', { name: 'Continue' })).toHaveProperty(
    'disabled',
    true,
  )
  valid = true
  await user.click(screen.getByRole('button', { name: 'Test connection' }))
  await waitFor(() =>
    expect(screen.getByRole('button', { name: 'Continue' })).toHaveProperty(
      'disabled',
      false,
    ),
  )
  await user.click(screen.getByRole('button', { name: 'Continue' }))
  expect(next).toHaveBeenCalledOnce()
})
