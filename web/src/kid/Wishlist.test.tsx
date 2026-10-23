import { readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { cleanup, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { axe } from '../test/axe'
import Wishlist, { wishlistPollMaxMs, wishlistPollMs } from './Wishlist'

type Wish = {
  id: number
  kind: 'movie' | 'show'
  tmdbId: number
  title: string | null
  state: 'waiting' | 'onTheWay' | 'ready' | 'denied'
  note: string | null
  plex: { webUrl: string; appUrl: string } | null
}

const waiting: Wish = {
  id: 1,
  kind: 'movie',
  tmdbId: 700,
  title: 'Tiny Tale',
  state: 'waiting',
  note: null,
  plex: null,
}

const webUrl =
  'https://app.plex.tv/desktop/#!/server/machine/details?key=%2Flibrary%2Fmetadata%2F102'
const appUrl =
  'plex://preplay/?metadataKey=%2Flibrary%2Fmetadata%2F102&metadataType=1&server=machine'

const ready: Wish = {
  ...waiting,
  id: 2,
  title: 'Ready Film',
  state: 'ready',
  plex: { webUrl, appUrl },
}

let wishlist: {
  remaining: number | null
  resetsAt: string | null
  items: Wish[]
}
let failures: number
let calls: number

beforeEach(() => {
  wishlist = { remaining: 2, resetsAt: null, items: [waiting] }
  failures = 0
  calls = 0
  vi.stubGlobal(
    'fetch',
    vi.fn(async () => {
      calls += 1
      if (failures > 0) {
        failures -= 1
        return Response.json({ title: 'Nope' }, { status: 503 })
      }
      return Response.json(wishlist)
    }),
  )
})

afterEach(() => {
  cleanup()
  vi.useRealTimers()
  vi.unstubAllGlobals()
  Object.defineProperty(document, 'hidden', {
    configurable: true,
    get: () => false,
  })
})

it('shows an encouraging empty wishlist', async () => {
  wishlist = { remaining: null, resetsAt: null, items: [] }
  const { container } = render(<Wishlist />)
  expect(
    await screen.findByText(
      'Nothing here yet. Find a movie or a show you would love, and ask for it.',
    ),
  ).toBeTruthy()
  expect(screen.getByText('You can ask for as many as you like.')).toBeTruthy()
  expect(screen.queryByRole('alert')).toBeNull()
  expect(await axe(container)).toHaveNoViolations()
})

it('renders a denial note as text and never as HTML', async () => {
  const note = '<img src=x onerror="alert(1)"><b>Maybe a cartoon</b>'
  wishlist = {
    remaining: 1,
    resetsAt: null,
    items: [{ ...waiting, state: 'denied', note }],
  }
  const { container } = render(<Wishlist />)
  const card = await screen.findByRole('listitem')
  expect(card.textContent).toContain(note)
  expect(card.textContent).toContain('Not this time')
  expect(card.querySelector('img')).toBeNull()
  expect(card.querySelector('b')).toBeNull()
  expect(card.querySelector('script')).toBeNull()
  expect(await axe(container)).toHaveNoViolations()
})

it('links a ready title to the Plex URLs from the API', async () => {
  const user = userEvent.setup()
  wishlist = { remaining: 2, resetsAt: null, items: [ready] }
  render(<Wishlist />)
  const web = await screen.findByRole('link', {
    name: 'Watch Ready Film in Plex',
  })
  const app = screen.getByRole('link', {
    name: 'Open Ready Film in the Plex app',
  })
  expect(web.getAttribute('href')).toBe(webUrl)
  expect(app.getAttribute('href')).toBe(appUrl)
  expect(document.body.innerHTML).not.toContain('X-Plex-Token')
  expect(document.body.innerHTML).not.toContain(':32400')
  await user.tab()
  expect(document.activeElement).toBe(web)
})

it('drops a Plex link that carries a token or a server port', async () => {
  wishlist = {
    remaining: null,
    resetsAt: null,
    items: [
      {
        ...ready,
        plex: {
          webUrl:
            'http://192.168.1.20:32400/web/index.html#!/server/machine/details?key=/library/metadata/102&X-Plex-Token=secret-token',
          appUrl: 'plex://preplay/?metadataKey=102&X-Plex-Token=secret-token',
        },
      },
    ],
  }
  render(<Wishlist />)
  expect(await screen.findByText('Ready to watch')).toBeTruthy()
  expect(screen.queryByRole('link')).toBeNull()
  expect(document.body.innerHTML).not.toContain('secret-token')
  expect(document.body.innerHTML).not.toContain('192.168.1.20')
})

it('shows how many wishes are left', async () => {
  wishlist = {
    remaining: 0,
    resetsAt: '2026-10-26T00:00:00.000Z',
    items: [],
  }
  render(<Wishlist />)
  const when = new Intl.DateTimeFormat(undefined, {
    dateStyle: 'medium',
  }).format(new Date('2026-10-26T00:00:00.000Z'))
  expect(
    await screen.findByText(
      `You have used your wishes for this week. More wishes start on ${when}.`,
    ),
  ).toBeTruthy()
})

it('updates a wish without reloading the page', async () => {
  vi.useFakeTimers({ shouldAdvanceTime: true })
  const { container } = render(<Wishlist />)
  const heading = await screen.findByRole('heading', { name: 'Your wishlist' })
  expect(screen.getByText('Waiting for a parent')).toBeTruthy()
  const firstFetch = calls

  wishlist = { remaining: 2, resetsAt: null, items: [ready] }
  await vi.advanceTimersByTimeAsync(wishlistPollMs)

  expect(await screen.findByText('Ready to watch')).toBeTruthy()
  expect(screen.queryByText('Waiting for a parent')).toBeNull()
  expect(screen.getByRole('heading', { name: 'Your wishlist' })).toBe(heading)
  expect(calls).toBe(firstFetch + 1)
  expect(container.ownerDocument.URL).toBe(document.URL)
})

it('pauses polling while the tab is hidden and refreshes when it returns', async () => {
  vi.useFakeTimers({ shouldAdvanceTime: true })
  let hidden = false
  Object.defineProperty(document, 'hidden', {
    configurable: true,
    get: () => hidden,
  })
  render(<Wishlist />)
  await screen.findByText('Waiting for a parent')
  const quiet = calls

  hidden = true
  document.dispatchEvent(new Event('visibilitychange'))
  await vi.advanceTimersByTimeAsync(wishlistPollMaxMs * 3)
  expect(calls).toBe(quiet)

  wishlist = { remaining: 2, resetsAt: null, items: [ready] }
  hidden = false
  document.dispatchEvent(new Event('visibilitychange'))
  expect(await screen.findByText('Ready to watch')).toBeTruthy()
  expect(calls).toBe(quiet + 1)
})

it('waits longer after a failed refresh', async () => {
  vi.useFakeTimers({ shouldAdvanceTime: true })
  render(<Wishlist />)
  await screen.findByText('Waiting for a parent')
  failures = 5

  await vi.advanceTimersByTimeAsync(wishlistPollMs)
  const afterFirstGap = calls
  await vi.advanceTimersByTimeAsync(wishlistPollMs)
  expect(calls).toBe(afterFirstGap)
  await vi.advanceTimersByTimeAsync(wishlistPollMs)
  expect(calls).toBe(afterFirstGap + 1)
})

it('does not hard-code a server address or a token', () => {
  const source = readFileSync(
    join(dirname(fileURLToPath(import.meta.url)), 'Wishlist.tsx'),
    'utf8',
  )
  expect(source).not.toMatch(/X-Plex-Token=/)
  expect(source).not.toMatch(/https?:\/\/\S*:32400/)
  expect(source).not.toMatch(/\b\d{1,3}(?:\.\d{1,3}){3}\b/)
  expect(wishlistPollMs).toBe(15_000)
  expect(wishlistPollMaxMs).toBe(60_000)
})
