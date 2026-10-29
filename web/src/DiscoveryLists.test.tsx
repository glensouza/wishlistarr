import { cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, expect, it, vi } from 'vitest'
import DiscoveryLists from './DiscoveryLists'

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

it('lets a parent add and save a list with a provider key', async () => {
  const writes: unknown[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (path: string, init?: RequestInit) => {
      if (path === '/api/v1/auth/antiforgery')
        return Response.json({ token: 'tok', headerName: 'X-CSRF-TOKEN' })
      if (init?.method === 'PUT') {
        writes.push(JSON.parse(init.body as string))
        return new Response(null, { status: 204 })
      }
      return Response.json({
        lists: [],
        hasTraktKey: false,
        hasMdbListKey: false,
      })
    }),
  )
  const user = userEvent.setup()
  render(<DiscoveryLists />)
  await user.click(await screen.findByRole('button', { name: 'Add list' }))
  await user.selectOptions(
    screen.getByLabelText('Provider'),
    'mdblist-external',
  )
  await user.type(screen.getByLabelText('List ID'), '42')
  await user.type(screen.getByLabelText('Row name'), 'Family picks')
  await user.type(screen.getByLabelText('MDBList API key'), 'test-key')
  await user.click(screen.getByRole('button', { name: 'Save discovery lists' }))
  await waitFor(() =>
    expect(writes).toEqual([
      {
        lists: [
          { provider: 'mdblist-external', listId: 42, name: 'Family picks' },
        ],
        traktKey: '',
        mdbListKey: 'test-key',
      },
    ]),
  )
  expect(screen.getByRole('status').textContent).toContain('saved')
})
