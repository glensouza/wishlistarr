import { cleanup, render, screen, waitFor } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import Wishlist from './Wishlist'
import { KidSessionProvider, useKidSession } from './KidSession'

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

function Probe() {
  const { session } = useKidSession()
  return (
    <>
      <p>{`session: ${session.status}`}</p>
      {session.status === 'in' && <Wishlist />}
    </>
  )
}

async function expectSignedOutAfter(signOutStatus: number) {
  const calls: string[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      calls.push(`${init?.method ?? 'GET'} ${url}`)
      if (url === '/api/v1/kid/me')
        return Response.json({ id: '1', name: 'Nia' })
      if (url === '/api/v1/kid/requests') {
        return Response.json({ title: 'Sign in' }, { status: 403 })
      }
      if (url === '/api/v1/auth/antiforgery') {
        return Response.json({ token: 't', headerName: 'X-CSRF' })
      }
      if (url === '/api/v1/kid/signout') {
        return new Response(null, { status: signOutStatus })
      }
      return new Response(null, { status: 404 })
    }),
  )
  render(
    <KidSessionProvider>
      <Probe />
    </KidSessionProvider>,
  )
  await screen.findByText('session: out')
  await waitFor(() => expect(calls).toContain('POST /api/v1/kid/signout'))
  return calls
}

it('ends the server session and the client session on a forbidden answer', async () => {
  const calls = await expectSignedOutAfter(204)
  expect(
    calls.filter((call) => call === 'GET /api/v1/kid/requests'),
  ).toHaveLength(1)
})

it('still returns to the picker when the server sign-out fails', async () => {
  const calls = await expectSignedOutAfter(500)
  expect(calls).toContain('POST /api/v1/kid/signout')
})
