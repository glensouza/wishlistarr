import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from '@testing-library/react'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import KidDevicesAdmin from './KidDevicesAdmin'
import type { KidDevice } from './kidDevices'
import { axe } from './test/axe'

type Call = { method: string; path: string; body: unknown; headers: Headers }

const profiles = [
  { id: 1, name: 'Ada', avatar: 'fox', isLocked: false },
  { id: 2, name: 'Bo', avatar: 'owl', isLocked: false },
]

const tablet: KidDevice = {
  id: 7,
  name: 'Tablet',
  profileIds: [1],
  isPaired: true,
  pairingCodeExpiresAt: null,
  createdAt: '2026-10-01T10:00:00Z',
  pairedAt: '2026-10-01T10:05:00Z',
  lastSeenAt: '2026-10-02T10:00:00Z',
  isCurrent: false,
}

let calls: Call[]
let devices: KidDevice[]

beforeEach(() => {
  calls = []
  devices = [tablet]
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
        headers: new Headers(init?.headers),
      })
      if (path === '/api/v1/kid-profiles') return Response.json(profiles)
      if (path === '/api/v1/kid-devices' && method === 'GET') {
        return Response.json(devices)
      }
      if (path === '/api/v1/kid-devices' && method === 'POST') {
        return Response.json(
          {
            device: { ...tablet, id: 8, name: 'Laptop', isPaired: false },
            code: 'K7QX-M2PD',
            expiresAt: '2026-10-02T10:10:00Z',
          },
          { status: 201 },
        )
      }
      if (path.endsWith('/pairing-code')) {
        return Response.json({
          code: 'R4TW-9ZHN',
          expiresAt: '2026-10-02T10:10:00Z',
        })
      }
      if (method === 'PUT') return Response.json(tablet)
      return new Response(null, { status: 204 })
    }),
  )
})

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

const writes = () => calls.filter((call) => call.method !== 'GET')

it('lists devices with their profiles and pairing state', async () => {
  devices = [
    { ...tablet, isCurrent: true },
    {
      ...tablet,
      id: 8,
      name: 'Laptop',
      profileIds: [],
      isPaired: false,
      lastSeenAt: null,
      pairingCodeExpiresAt: '2026-10-02T10:10:00Z',
    },
  ]
  const { container } = render(<KidDevicesAdmin />)

  expect(await screen.findByText('Profiles: Ada')).toBeTruthy()
  expect(screen.getByText('(this device)')).toBeTruthy()
  expect(screen.getByText('No profiles')).toBeTruthy()
  expect(screen.getByText(/^Waiting to be paired until/)).toBeTruthy()
  expect(screen.getByText(/^Last used/)).toBeTruthy()
  expect(await axe(container)).toHaveNoViolations()
})

it('adds a device with profiles and shows the pairing code once', async () => {
  devices = []
  render(<KidDevicesAdmin />)
  fireEvent.click(await screen.findByRole('button', { name: 'Add device' }))

  fireEvent.change(screen.getByLabelText('Device name'), {
    target: { value: 'Laptop' },
  })
  fireEvent.click(screen.getByLabelText('Bo'))
  fireEvent.click(screen.getByRole('button', { name: 'Save' }))

  expect(await screen.findByText('K7QX-M2PD')).toBeTruthy()
  expect(
    screen.getByRole('heading', { name: 'Pairing code for Laptop' }),
  ).toBeTruthy()
  expect(writes()[0]).toMatchObject({
    method: 'POST',
    path: '/api/v1/kid-devices',
    body: { name: 'Laptop', profileIds: [2] },
  })
  expect(writes()[0].headers.get('X-CSRF-TOKEN')).toBe('tok')

  fireEvent.click(screen.getByRole('button', { name: 'Done' }))
  expect(screen.queryByText('K7QX-M2PD')).toBeNull()
})

it('changes the profiles assigned to a device', async () => {
  render(<KidDevicesAdmin />)
  fireEvent.click(await screen.findByRole('button', { name: 'Edit Tablet' }))

  expect(screen.getByLabelText<HTMLInputElement>('Ada').checked).toBe(true)
  fireEvent.click(screen.getByLabelText('Ada'))
  fireEvent.click(screen.getByLabelText('Bo'))
  fireEvent.click(screen.getByRole('button', { name: 'Save' }))

  await waitFor(() => expect(writes()).toHaveLength(1))
  expect(writes()[0]).toMatchObject({
    method: 'PUT',
    path: '/api/v1/kid-devices/7',
    body: { name: 'Tablet', profileIds: [2] },
  })
})

it('issues a new pairing code for a device', async () => {
  render(<KidDevicesAdmin />)
  fireEvent.click(
    await screen.findByRole('button', { name: 'New pairing code for Tablet' }),
  )

  expect(await screen.findByText('R4TW-9ZHN')).toBeTruthy()
  expect(writes()[0]).toMatchObject({
    method: 'POST',
    path: '/api/v1/kid-devices/7/pairing-code',
  })
})

it('revokes a device only after confirming', async () => {
  render(<KidDevicesAdmin />)
  fireEvent.click(await screen.findByRole('button', { name: 'Revoke Tablet' }))

  expect(
    screen.getByRole('alertdialog', { name: 'Revoke Tablet' }),
  ).toBeTruthy()
  expect(writes()).toHaveLength(0)
  fireEvent.click(screen.getByRole('button', { name: 'Keep device' }))
  expect(screen.queryByRole('alertdialog')).toBeNull()

  fireEvent.click(screen.getByRole('button', { name: 'Revoke Tablet' }))
  fireEvent.click(screen.getByRole('button', { name: 'Confirm revoke' }))
  await waitFor(() => expect(writes()).toHaveLength(1))
  expect(writes()[0]).toMatchObject({
    method: 'DELETE',
    path: '/api/v1/kid-devices/7',
  })
})
