import { cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import TrailerSetting, { trailerSettingKey } from './TrailerSetting'

type Row = { key: string; isSecret: boolean; value: string | null }
type Call = { method: string; path: string; body: unknown }

let rows: Row[]
let putStatus: number
let calls: Call[]
let settingsReply: () => Promise<Response>

beforeEach(() => {
  rows = []
  putStatus = 204
  calls = []
  settingsReply = async () => Response.json(rows)
  vi.stubGlobal(
    'fetch',
    vi.fn(async (path: string, init?: RequestInit) => {
      if (path === '/api/v1/auth/antiforgery') {
        return Response.json({ token: 'tok', headerName: 'X-CSRF-TOKEN' })
      }
      const call: Call = {
        method: init?.method ?? 'GET',
        path,
        body: init?.body ? JSON.parse(init.body as string) : undefined,
      }
      calls.push(call)
      if (path === '/api/v1/settings/') return settingsReply()
      return new Response(null, { status: putStatus })
    }),
  )
})

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

const checkbox = () =>
  screen.findByRole('checkbox', { name: 'Let kids play YouTube trailers' })

it('stays off when the setting is missing, false, or not a boolean', async () => {
  const { unmount } = render(<TrailerSetting />)
  expect(((await checkbox()) as HTMLInputElement).checked).toBe(false)
  expect(
    screen.getByText(
      'When this is off, kids never see a trailer and YouTube is not loaded.',
    ),
  ).toBeTruthy()

  unmount()
  rows = [{ key: trailerSettingKey, isSecret: false, value: 'false' }]
  render(<TrailerSetting />)
  expect(((await checkbox()) as HTMLInputElement).checked).toBe(false)

  cleanup()
  rows = [{ key: trailerSettingKey, isSecret: true, value: 'yes' }]
  render(<TrailerSetting />)
  expect(((await checkbox()) as HTMLInputElement).checked).toBe(false)
})

it('turns trailers on only when the stored value is true', async () => {
  rows = [{ key: trailerSettingKey, isSecret: false, value: 'true' }]
  render(<TrailerSetting />)
  expect(((await checkbox()) as HTMLInputElement).checked).toBe(true)
})

it('does not show the checkbox as on while the setting is still loading', async () => {
  let release: ((response: Response) => void) | undefined
  settingsReply = () =>
    new Promise<Response>((resolve) => {
      release = resolve
    })
  render(<TrailerSetting />)

  expect(
    screen.queryByRole('checkbox', { name: 'Let kids play YouTube trailers' }),
  ).toBeNull()
  release?.(
    Response.json([{ key: trailerSettingKey, isSecret: false, value: 'true' }]),
  )
  expect(((await checkbox()) as HTMLInputElement).checked).toBe(true)
})

it('saves true and then false', async () => {
  const user = userEvent.setup()
  render(<TrailerSetting />)
  const box = (await checkbox()) as HTMLInputElement

  await user.click(box)
  await waitFor(() => expect(box.disabled).toBe(false))
  expect(box.checked).toBe(true)
  await user.click(box)
  await waitFor(() => expect(box.checked).toBe(false))

  const puts = calls.filter((call) => call.method === 'PUT')
  expect(puts.map((call) => call.path)).toEqual([
    `/api/v1/settings/${trailerSettingKey}`,
    `/api/v1/settings/${trailerSettingKey}`,
  ])
  expect(puts.map((call) => call.body)).toEqual([
    { value: 'true', isSecret: false },
    { value: 'false', isSecret: false },
  ])
})

it('puts the checkbox back when saving fails', async () => {
  const user = userEvent.setup()
  rows = [{ key: trailerSettingKey, isSecret: false, value: 'true' }]
  putStatus = 500
  render(<TrailerSetting />)
  const box = (await checkbox()) as HTMLInputElement

  await user.click(box)

  expect((await screen.findByRole('alert')).textContent).toBe(
    'We could not save that. Try again.',
  )
  expect(box.checked).toBe(true)
})
