import { act, cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { axe } from '../test/axe'
import Wishlist, { wishlistPollMaxMs, wishlistPollMs } from './Wishlist'

const signOut = vi.hoisted(() => vi.fn())

vi.mock('./KidSession', () => ({
  useKidSession: () => ({ signOut }),
}))

type Wish = {
  id: number
  kind: 'movie' | 'show'
  tmdbId: number
  title: string | null
  state: string
  note: string | null
  plex: { webUrl: string } | null
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

const ready: Wish = {
  ...waiting,
  id: 2,
  title: 'Ready Film',
  state: 'ready',
  plex: { webUrl },
}

let wishlist: {
  remaining: number | null
  resetsAt: string | null
  items: Wish[]
}
let failures: number
let failStatus: number
let calls: number
let hold: Promise<void> | null

beforeEach(() => {
  wishlist = { remaining: 2, resetsAt: null, items: [waiting] }
  failures = 0
  failStatus = 503
  calls = 0
  hold = null
  vi.stubGlobal(
    'fetch',
    vi.fn(async () => {
      calls += 1
      if (hold) {
        const pending = hold
        hold = null
        await pending
      }
      if (failures > 0) {
        failures -= 1
        return Response.json({ title: 'Nope' }, { status: failStatus })
      }
      return Response.json(wishlist)
    }),
  )
})

afterEach(() => {
  cleanup()
  signOut.mockReset()
  signOut.mockResolvedValue(undefined)
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
  expect(web.getAttribute('href')).toBe(webUrl)
  expect(screen.getAllByRole('link')).toHaveLength(1)
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
            'http://plex.example.test:32400/web/index.html#!/server/machine/details?key=/library/metadata/102&X-Plex-Token=fake-token-value',
        },
      },
    ],
  }
  render(<Wishlist />)
  expect(await screen.findByText('Ready to watch')).toBeTruthy()
  expect(screen.queryByRole('link')).toBeNull()
  expect(document.body.innerHTML).not.toContain('fake-token-value')
  expect(document.body.innerHTML).not.toContain('plex.example.test')
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

it('caps the wait after repeated failures', async () => {
  vi.useFakeTimers({ shouldAdvanceTime: true })
  render(<Wishlist />)
  await screen.findByText('Waiting for a parent')
  failures = 100
  const base = calls

  await vi.advanceTimersByTimeAsync(wishlistPollMs)
  await vi.advanceTimersByTimeAsync(wishlistPollMs * 2)
  await vi.advanceTimersByTimeAsync(wishlistPollMaxMs)
  expect(calls).toBe(base + 3)

  await vi.advanceTimersByTimeAsync(wishlistPollMaxMs)
  expect(calls).toBe(base + 4)
  await vi.advanceTimersByTimeAsync(wishlistPollMaxMs)
  expect(calls).toBe(base + 5)
})

it('stops polling and signs the kid out when the session has ended', async () => {
  vi.useFakeTimers({ shouldAdvanceTime: true })
  render(<Wishlist />)
  await screen.findByText('Waiting for a parent')
  failures = 100
  failStatus = 401
  const base = calls

  await vi.advanceTimersByTimeAsync(wishlistPollMs)
  expect(calls).toBe(base + 1)
  expect(signOut).toHaveBeenCalledTimes(1)

  await vi.advanceTimersByTimeAsync(wishlistPollMaxMs * 5)
  expect(calls).toBe(base + 1)
  expect(signOut).toHaveBeenCalledTimes(1)
})

it('also stops on a forbidden answer', async () => {
  vi.useFakeTimers({ shouldAdvanceTime: true })
  failures = 100
  failStatus = 403
  render(<Wishlist />)
  await vi.advanceTimersByTimeAsync(0)
  await vi.advanceTimersByTimeAsync(wishlistPollMaxMs * 5)
  expect(calls).toBe(1)
  expect(signOut).toHaveBeenCalledTimes(1)
})

it('does not fetch again after it is removed', async () => {
  vi.useFakeTimers({ shouldAdvanceTime: true })
  const { unmount } = render(<Wishlist />)
  await screen.findByText('Waiting for a parent')
  const base = calls
  unmount()
  await vi.advanceTimersByTimeAsync(wishlistPollMaxMs * 5)
  expect(calls).toBe(base)
  expect(signOut).not.toHaveBeenCalled()
})

it('keeps one request chain when the tab is hidden and shown during a fetch', async () => {
  vi.useFakeTimers({ shouldAdvanceTime: true })
  let hidden = false
  Object.defineProperty(document, 'hidden', {
    configurable: true,
    get: () => hidden,
  })
  render(<Wishlist />)
  await screen.findByText('Waiting for a parent')

  let release = () => {}
  hold = new Promise<void>((resolve) => {
    release = resolve
  })
  await vi.advanceTimersByTimeAsync(wishlistPollMs)
  const pending = calls

  hidden = true
  document.dispatchEvent(new Event('visibilitychange'))
  hidden = false
  document.dispatchEvent(new Event('visibilitychange'))
  expect(calls).toBe(pending)

  await act(async () => release())
  await vi.advanceTimersByTimeAsync(wishlistPollMs * 3)
  expect(calls).toBe(pending + 3)
})

it('shows a neutral badge for a state it does not know', async () => {
  wishlist = {
    remaining: 1,
    resetsAt: null,
    items: [{ ...waiting, state: 'somethingNew' }],
  }
  render(<Wishlist />)
  const badge = await screen.findByText('Checking on it')
  expect(badge.className).toContain('badge-unknown')
})

it('announces a changed state with the title and not on first load', async () => {
  vi.useFakeTimers({ shouldAdvanceTime: true })
  const { container } = render(<Wishlist />)
  await screen.findByText('Waiting for a parent')
  const live = container.querySelector('.visually-hidden[aria-live="polite"]')
  expect(live?.textContent).toBe('')
  expect(container.querySelectorAll('.badge[aria-live]')).toHaveLength(0)

  await vi.advanceTimersByTimeAsync(wishlistPollMs)
  expect(live?.textContent).toBe('')

  wishlist = {
    remaining: 2,
    resetsAt: null,
    items: [{ ...waiting, state: 'ready', plex: null }],
  }
  await vi.advanceTimersByTimeAsync(wishlistPollMs)
  expect(live?.textContent).toBe('Tiny Tale: Ready to watch')
})

it('offers a retry when the first load fails', async () => {
  const user = userEvent.setup()
  failures = 1
  render(<Wishlist />)
  expect(await screen.findByRole('alert')).toBeTruthy()
  await user.click(screen.getByRole('button', { name: 'Try again' }))
  expect(await screen.findByText('Waiting for a parent')).toBeTruthy()
  expect(screen.queryByRole('alert')).toBeNull()
})

it('keeps the list and says so when a later refresh fails', async () => {
  vi.useFakeTimers({ shouldAdvanceTime: true })
  render(<Wishlist />)
  await screen.findByText('Waiting for a parent')
  expect(screen.queryByText('We could not check for updates.')).toBeNull()

  failures = 1
  await vi.advanceTimersByTimeAsync(wishlistPollMs)
  expect(
    await screen.findByText('We could not check for updates.'),
  ).toBeTruthy()
  expect(screen.getByText('Waiting for a parent')).toBeTruthy()

  await vi.advanceTimersByTimeAsync(wishlistPollMs * 3)
  await waitFor(() =>
    expect(screen.queryByText('We could not check for updates.')).toBeNull(),
  )
})

it('retries a failed refresh on request', async () => {
  vi.useFakeTimers({ shouldAdvanceTime: true })
  render(<Wishlist />)
  await screen.findByText('Waiting for a parent')
  failures = 1
  await vi.advanceTimersByTimeAsync(wishlistPollMs)
  const button = await screen.findByRole('button', { name: 'Try again' })
  await act(async () => button.click())
  await vi.advanceTimersByTimeAsync(0)
  expect(screen.queryByText('We could not check for updates.')).toBeNull()
})

it('says how many wishes are left', async () => {
  wishlist = { remaining: 3, resetsAt: null, items: [] }
  const { unmount } = render(<Wishlist />)
  expect(await screen.findByText('3 wishes left this week')).toBeTruthy()
  unmount()

  wishlist = { remaining: 1, resetsAt: null, items: [] }
  render(<Wishlist />)
  expect(await screen.findByText('1 wish left this week')).toBeTruthy()
})

it('ignores a failed sign-out call when the session has ended', async () => {
  vi.useFakeTimers({ shouldAdvanceTime: true })
  signOut.mockRejectedValue(new Error('offline'))
  failures = 100
  failStatus = 401
  render(<Wishlist />)
  await vi.advanceTimersByTimeAsync(0)
  await vi.advanceTimersByTimeAsync(wishlistPollMaxMs * 2)
  expect(signOut).toHaveBeenCalledTimes(1)
  expect(calls).toBe(1)
})

it('tells screen readers when a refresh fails', async () => {
  vi.useFakeTimers({ shouldAdvanceTime: true })
  render(<Wishlist />)
  await screen.findByText('Waiting for a parent')
  failures = 1
  await vi.advanceTimersByTimeAsync(wishlistPollMs)
  const notice = (
    await screen.findByText('We could not check for updates.')
  ).closest('[role="status"]')
  expect(notice).not.toBeNull()
  expect(notice?.querySelector('button')).not.toBeNull()
})

it('announces the same change again after the text was cleared', async () => {
  vi.useFakeTimers({ shouldAdvanceTime: true })
  const { container } = render(<Wishlist />)
  await screen.findByText('Waiting for a parent')
  const live = container.querySelector('.visually-hidden[aria-live="polite"]')
  const readyItem = { ...waiting, state: 'ready', plex: null }

  const expectAnnounced = async (text: string) => {
    await vi.advanceTimersByTimeAsync(wishlistPollMs)
    await waitFor(() => expect(live?.textContent).toBe(text))
    await act(async () => {
      await vi.advanceTimersByTimeAsync(3_500)
    })
    expect(live?.textContent).toBe('')
  }

  wishlist = { remaining: 2, resetsAt: null, items: [readyItem] }
  await expectAnnounced('Tiny Tale: Ready to watch')
  wishlist = { remaining: 2, resetsAt: null, items: [waiting] }
  await expectAnnounced('Tiny Tale: Waiting for a parent')
  wishlist = { remaining: 2, resetsAt: null, items: [readyItem] }
  await expectAnnounced('Tiny Tale: Ready to watch')
})

it('keeps a single request chain when the tab becomes visible while a timer is waiting', async () => {
  vi.useFakeTimers({ shouldAdvanceTime: true })
  render(<Wishlist />)
  await screen.findByText('Waiting for a parent')
  const base = calls

  await vi.advanceTimersByTimeAsync(wishlistPollMs / 3)
  document.dispatchEvent(new Event('visibilitychange'))
  await vi.advanceTimersByTimeAsync(0)
  expect(calls).toBe(base + 1)

  await vi.advanceTimersByTimeAsync(wishlistPollMs * 4)
  expect(calls).toBe(base + 5)
})
