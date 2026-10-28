import { readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { cleanup, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, expect, it, vi } from 'vitest'
import NotificationSettings from './NotificationSettings'
import DevicePush from './kid/DevicePush'

const settings = {
  publicBaseUrl: 'https://wishlist.example',
  providers: [],
}

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
  Reflect.deleteProperty(navigator, 'serviceWorker')
})

function stubFetch(webPush: unknown) {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (path: string, init?: RequestInit) => {
      if (path === '/api/v1/auth/antiforgery') {
        return Response.json({ token: 'tok', headerName: 'X-CSRF-TOKEN' })
      }
      const method = init?.method ?? 'GET'
      if (method === 'GET' && path === '/api/v1/notifications') {
        return Response.json(settings)
      }
      if (method === 'GET' && path === '/api/v1/notifications/web-push') {
        return Response.json(webPush)
      }
      if (method === 'GET' && path === '/api/v1/kid/web-push') {
        return Response.json(webPush)
      }
      return new Response(null, { status: 204 })
    }),
  )
}

it('explains when the parent browser cannot show notifications', async () => {
  stubFetch({ publicKey: 'Btest', subscriptions: [] })
  const user = userEvent.setup()
  render(<NotificationSettings />)
  await user.click(screen.getByRole('button', { name: 'Manage notifications' }))
  await user.click(
    await screen.findByRole('button', { name: 'Turn on notifications' }),
  )
  expect((await screen.findByRole('alert')).textContent).toBe(
    'This browser cannot show notifications.',
  )
  const fetchMock = vi.mocked(fetch)
  expect(
    fetchMock.mock.calls.some(
      ([path, init]) =>
        path === '/api/v1/notifications/web-push' && init?.method === 'POST',
    ),
  ).toBe(false)
})

it('explains when notifications are blocked for this browser', async () => {
  stubFetch({ publicKey: 'Btest', subscriptions: [] })
  vi.stubGlobal('Notification', { permission: 'denied' })
  vi.stubGlobal('PushManager', function PushManager() {})
  Object.defineProperty(navigator, 'serviceWorker', {
    configurable: true,
    value: {},
  })
  const user = userEvent.setup()
  render(<NotificationSettings />)
  await user.click(screen.getByRole('button', { name: 'Manage notifications' }))
  await user.click(
    await screen.findByRole('button', { name: 'Turn on notifications' }),
  )
  expect((await screen.findByRole('alert')).textContent).toBe(
    'Notifications are blocked for this browser. Allow them in the browser settings, then try again.',
  )
})

it('explains when a paired device cannot subscribe', async () => {
  stubFetch({ publicKey: 'Btest', subscribed: false })
  const user = userEvent.setup()
  render(<DevicePush />)
  await user.click(
    await screen.findByRole('button', { name: 'Turn on ready notices' }),
  )
  expect((await screen.findByRole('alert')).textContent).toBe(
    'This browser cannot show notifications.',
  )
})

it('keeps titles out of the service worker and shows a generic fallback', () => {
  const source = readFileSync(
    join(dirname(fileURLToPath(import.meta.url)), '../public/sw.js'),
    'utf8',
  )
  expect(source).toContain("self.addEventListener('push'")
  expect(source).toContain('showNotification')
  expect(source).toContain("title = 'Wishlistarr'")
  expect(source).toContain("body = 'Something new is ready to watch.'")
  expect(source).not.toContain('Spirited Away')
  const helper = readFileSync(
    join(dirname(fileURLToPath(import.meta.url)), 'webPush.ts'),
    'utf8',
  )
  expect(helper).toContain("register('/sw.js')")
})
