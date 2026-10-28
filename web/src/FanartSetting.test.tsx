import { cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, expect, it, vi } from 'vitest'
import FanartSetting from './FanartSetting'

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

it('saves and removes the optional key without displaying a stored secret', async () => {
  const writes: unknown[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (path: string, init?: RequestInit) => {
      if (path === '/api/v1/auth/antiforgery')
        return Response.json({ token: 'test', headerName: 'X-CSRF-TOKEN' })
      if (path === '/api/v1/settings/') return Response.json([])
      writes.push(JSON.parse(init?.body as string))
      return new Response(null, { status: 204 })
    }),
  )
  const user = userEvent.setup()
  render(<FanartSetting />)
  const input = await screen.findByLabelText('fanart.tv project API key')
  await user.type(input, 'demo-key')
  await user.click(screen.getByRole('button', { name: 'Save key' }))
  await screen.findByText('A project key is saved.')
  expect((input as HTMLInputElement).value).toBe('')
  await user.click(screen.getByRole('button', { name: 'Remove key' }))
  await screen.findByText('No project key is saved.')
  const personal = screen.getByLabelText('fanart.tv personal API key')
  await user.type(personal, 'personal-key')
  await user.click(screen.getByRole('button', { name: 'Save personal key' }))
  await screen.findByText('A personal key is saved.')
  expect((personal as HTMLInputElement).value).toBe('')
  await waitFor(() =>
    expect(writes).toEqual([
      { value: 'demo-key', isSecret: true },
      { value: '', isSecret: true },
      { value: 'personal-key', isSecret: true },
    ]),
  )
})
