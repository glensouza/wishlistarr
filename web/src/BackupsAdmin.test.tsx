import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from '@testing-library/react'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import BackupsAdmin from './BackupsAdmin'
import { axe } from './test/axe'

type Call = { method: string; path: string; body: unknown; headers: Headers }

let calls: Call[]
let restorePending = false

beforeEach(() => {
  calls = []
  restorePending = false
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
        body: init?.body instanceof FormData ? init.body : init?.body,
        headers: new Headers(init?.headers),
      })
      if (path === '/api/v1/backups' && method === 'GET') {
        return Response.json({
          retention: 7,
          restorePending,
          backups: [
            {
              name: 'wishlistarr-20261022T153045123Z-000001.zip',
              createdAt: '2026-10-22T15:30:45.123Z',
              sizeBytes: 1200,
            },
          ],
        })
      }
      if (path === '/api/v1/jobs/database-backup/run') {
        return Response.json({ runId: 4 }, { status: 202 })
      }
      if (path.startsWith('/api/v1/jobs/database-backup/runs')) {
        return Response.json([{ id: 4, outcome: 'succeeded' }])
      }
      if (path === '/api/v1/backups/restore') {
        restorePending = true
        return Response.json({ restorePending: true }, { status: 202 })
      }
      return new Response(null, { status: 404 })
    }),
  )
})

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

it('warns that a backup contains secrets and can download one', async () => {
  const { container } = render(<BackupsAdmin />)
  expect(
    await screen.findByText(/keys that unlock saved secrets/i),
  ).toBeTruthy()
  expect(screen.getByText(/newest 7 backups/i)).toBeTruthy()
  const download = await screen.findByRole('link', { name: 'Download' })
  expect(download.getAttribute('href')).toBe(
    '/api/v1/backups/wishlistarr-20261022T153045123Z-000001.zip/download',
  )
  expect(await axe(container)).toHaveNoViolations()
})

it('starts a backup with the antiforgery token', async () => {
  render(<BackupsAdmin />)
  await screen.findByRole('link', { name: 'Download' })
  fireEvent.click(screen.getByRole('button', { name: 'Back up now' }))
  await waitFor(() =>
    expect(
      calls.some((call) => call.path === '/api/v1/jobs/database-backup/run'),
    ).toBe(true),
  )
  const run = calls.find(
    (call) => call.path === '/api/v1/jobs/database-backup/run',
  )
  expect(run?.method).toBe('POST')
  expect(run?.headers.get('X-CSRF-TOKEN')).toBe('tok')
})

it('uploads a restore with the antiforgery token and no json body', async () => {
  render(<BackupsAdmin />)
  await screen.findByRole('link', { name: 'Download' })
  const input = screen.getByLabelText('Backup file')
  const file = new File(['zip'], 'backup.zip', { type: 'application/zip' })
  fireEvent.change(input, { target: { files: [file] } })
  fireEvent.submit(input.closest('form')!)
  await waitFor(() =>
    expect(calls.some((call) => call.path === '/api/v1/backups/restore')).toBe(
      true,
    ),
  )
  const restore = calls.find((call) => call.path === '/api/v1/backups/restore')
  expect(restore?.headers.get('X-CSRF-TOKEN')).toBe('tok')
  expect(restore?.headers.get('Content-Type')).toBeNull()
  expect(restore?.body).toBeInstanceOf(FormData)
  expect(await screen.findByText(/next time Wishlistarr starts/i)).toBeTruthy()
})
