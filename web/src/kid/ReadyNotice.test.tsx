import { useRef } from 'react'
import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, expect, it, vi } from 'vitest'
import ReadyNotice from './ReadyNotice'
import { useKidSpatialNav } from './useKidSpatialNav'

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

function stubNotices(body: unknown, status = 200) {
  const calls: { path: string; method: string }[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (path: string, init?: RequestInit) => {
      if (path === '/api/v1/auth/antiforgery') {
        return Response.json({ token: 'tok', headerName: 'X-CSRF-TOKEN' })
      }
      calls.push({ path, method: init?.method ?? 'GET' })
      if (path === '/api/v1/kid/ready-notices') {
        return Response.json(body, { status })
      }
      if (String(path).endsWith('/dismiss')) {
        return new Response(null, { status: 204 })
      }
      return Response.json({}, { status: 404 })
    }),
  )
  return calls
}

function place(
  element: HTMLElement,
  left: number,
  top: number,
  width = 160,
  height = 48,
) {
  const rect = {
    x: left,
    y: top,
    left,
    top,
    right: left + width,
    bottom: top + height,
    width,
    height,
    toJSON() {
      return rect
    },
  }
  element.getBoundingClientRect = () => rect
}

function Harness() {
  const rootRef = useRef<HTMLElement>(null)
  useKidSpatialNav(rootRef)
  return (
    <section ref={rootRef}>
      <button type="button">Ask for Tiny Tale</button>
      <ReadyNotice />
    </section>
  )
}

it('moves to the ready notice with the arrow keys and dismisses it', async () => {
  const calls = stubNotices([{ id: 4, requestId: 9, title: 'Spirited Away' }])
  const user = userEvent.setup()
  render(<Harness />)
  const ask = await screen.findByRole('button', { name: 'Ask for Tiny Tale' })
  const dismiss = await screen.findByRole('button', { name: 'Got it' })
  expect(screen.getByRole('heading', { name: 'New to watch' })).toBeTruthy()
  expect(screen.getByText('Spirited Away is ready to watch.')).toBeTruthy()
  place(ask, 0, 0)
  place(dismiss, 0, 80)
  ask.focus()
  fireEvent.keyDown(ask, { key: 'ArrowDown' })
  expect(document.activeElement).toBe(dismiss)
  await user.click(dismiss)
  expect(screen.queryByRole('heading', { name: 'New to watch' })).toBeNull()
  expect(
    calls.some(
      (call) =>
        call.path === '/api/v1/kid/ready-notices/4/dismiss' &&
        call.method === 'POST',
    ),
  ).toBe(true)
})

it('shows a generic notice when the signed-in profile is not given a title', async () => {
  stubNotices([{ id: 5, requestId: 9, title: null }])
  render(<Harness />)
  expect(
    await screen.findByText('Something new is ready to watch.'),
  ).toBeTruthy()
  expect(screen.queryByText('Spirited Away')).toBeNull()
})

it('shows nothing when the notice response is not a list', async () => {
  stubNotices({ id: 1, state: 'pending', requestCount: 1 }, 201)
  render(<Harness />)
  await screen.findByRole('button', { name: 'Ask for Tiny Tale' })
  await waitFor(() => {
    expect(
      vi
        .mocked(fetch)
        .mock.calls.some(([path]) => path === '/api/v1/kid/ready-notices'),
    ).toBe(true)
  })
  expect(screen.queryByRole('heading', { name: 'New to watch' })).toBeNull()
})
