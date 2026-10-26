import { cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { MemoryRouter } from 'react-router-dom'
import { axe } from '../test/axe'
import Search, { searchDebounceMs, searchMaxPage } from './Search'

const renderSearch = () =>
  render(
    <MemoryRouter>
      <Search />
    </MemoryRouter>,
  )

type Item = {
  kind: 'movie' | 'show'
  tmdbId: number
  title: string
  year: string | null
  overview: string | null
  posterUrl: string | null
  availability: 'inLibrary' | 'notInLibrary' | null
  needsParent: boolean
}

type Call = {
  method: string
  path: string
  body: unknown
  signal?: AbortSignal
}

const tiny: Item = {
  kind: 'movie',
  tmdbId: 800,
  title: 'Tiny Tale',
  year: '2019',
  overview: 'Gentle.',
  posterUrl: '/api/v1/kid/images/tmdb/w342/tiny.jpg',
  availability: 'inLibrary',
  needsParent: false,
}

const bluey: Item = {
  kind: 'show',
  tmdbId: 82728,
  title: 'Bluey',
  year: '2018',
  overview: null,
  posterUrl: null,
  availability: 'notInLibrary',
  needsParent: false,
}

const masked: Item = {
  kind: 'movie',
  tmdbId: 802,
  title: 'Unrated Short',
  year: '2020',
  overview: null,
  posterUrl: null,
  availability: null,
  needsParent: true,
}

let calls: Call[]
let searchReply: (call: Call) => Promise<Response>
let requestReply: (call: Call) => Promise<Response>

const page = (results: Item[], hasMore = false, number = 1, query = 'tiny') =>
  Response.json({ query, page: number, hasMore, results })

const queryOf = (call: Call) =>
  new URL(call.path, 'http://localhost').searchParams.get('q') ?? ''

const searches = () =>
  calls.filter((call) => call.path.startsWith('/api/v1/kid/search'))

beforeEach(() => {
  calls = []
  searchReply = async () => page([tiny, bluey])
  requestReply = async () =>
    Response.json({ id: 1, state: 'pending', requestCount: 1 }, { status: 201 })
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
        signal: init?.signal ?? undefined,
      }
      calls.push(call)
      return path.startsWith('/api/v1/kid/search')
        ? searchReply(call)
        : requestReply(call)
    }),
  )
})

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

const type = async (text: string) => {
  const user = userEvent.setup()
  await user.type(screen.getByRole('searchbox'), text)
  return user
}

it('waits for typing to pause and sends one request for the final text', async () => {
  renderSearch()
  await type('matrix')

  expect(await screen.findByText('Tiny Tale (2019)')).toBeTruthy()
  expect(searches()).toHaveLength(1)
  expect(searches()[0].path).toBe('/api/v1/kid/search?q=matrix&page=1')
  expect(searchDebounceMs).toBeGreaterThan(0)
})

it('does not search for a single letter and explains why', async () => {
  renderSearch()
  await type('a')

  expect(screen.getByText('Type at least 2 letters.')).toBeTruthy()
  await new Promise((resolve) => setTimeout(resolve, searchDebounceMs + 100))
  expect(searches()).toHaveLength(0)
})

it('cancels a stale request and shows only the latest answer', async () => {
  searchReply = (call) =>
    call.path.includes('q=ab&')
      ? new Promise<Response>((_, reject) =>
          call.signal?.addEventListener('abort', () =>
            reject(new DOMException('aborted', 'AbortError')),
          ),
        )
      : Promise.resolve(page([bluey]))
  renderSearch()
  const user = await type('ab')
  await waitFor(() => expect(searches()).toHaveLength(1))

  await user.type(screen.getByRole('searchbox'), 'c')

  expect(await screen.findByText('Bluey (2018)')).toBeTruthy()
  expect(searches()[0].signal?.aborted).toBe(true)
  expect(searches()[1].signal?.aborted).toBe(false)
  expect(screen.queryByText('Tiny Tale (2019)')).toBeNull()
  expect(screen.queryByRole('alert')).toBeNull()
})

it('shows big results with availability when the library is known', async () => {
  const { container } = renderSearch()
  await type('tiny')

  await screen.findByText('Tiny Tale (2019)')
  expect(
    screen.getByRole('link', { name: 'Tiny Tale (2019)' }).getAttribute('href'),
  ).toBe('/title/movie/800')
  expect(screen.getByText('Ready to watch')).toBeTruthy()
  expect(screen.getByText('Not here yet')).toBeTruthy()
  expect(container.querySelector('img')?.getAttribute('src')).toBe(
    '/api/v1/kid/images/tmdb/w342/tiny.jpg',
  )
  expect(await axe(container)).toHaveNoViolations()
})

it('omits availability when the library index cannot say', async () => {
  searchReply = async () => page([{ ...tiny, availability: null }])
  renderSearch()
  await type('tiny')

  await screen.findByText('Tiny Tale (2019)')
  expect(screen.queryByText('Ready to watch')).toBeNull()
  expect(screen.queryByText('Not here yet')).toBeNull()
})

it('shows a friendly empty state', async () => {
  searchReply = async (call) => page([], false, 1, queryOf(call))
  const { container } = renderSearch()
  await type('zzzz')

  expect(
    await screen.findByText(
      'We could not find anything for "zzzz". Try another word or check the spelling.',
    ),
  ).toBeTruthy()
  expect(await axe(container)).toHaveNoViolations()
})

it('sends the request and shows what happened', async () => {
  renderSearch()
  const user = await type('tiny')
  await screen.findByText('Tiny Tale (2019)')

  await user.click(screen.getByRole('button', { name: 'Ask for Tiny Tale' }))

  expect(
    await screen.findByText('Asked! A parent will take a look.'),
  ).toBeTruthy()
  const made = calls.find((call) => call.path === '/api/v1/kid/requests')
  expect(made?.method).toBe('POST')
  expect(made?.body).toEqual({ kind: 'movie', tmdbId: 800 })
  expect(
    screen.getByRole('button', { name: 'Ask for Tiny Tale' }),
  ).toHaveProperty('disabled', true)
})

it('says so when the title went straight onto the list', async () => {
  requestReply = async () =>
    Response.json({ id: 1, state: 'labeled', requestCount: 1 }, { status: 201 })
  renderSearch()
  const user = await type('tiny')
  await screen.findByText('Tiny Tale (2019)')

  await user.click(screen.getByRole('button', { name: 'Ask for Tiny Tale' }))

  expect(await screen.findByText('Added to your list!')).toBeTruthy()
})

it('says so when the title was already asked for', async () => {
  requestReply = async () =>
    Response.json({ id: 1, state: 'pending', requestCount: 1 }, { status: 200 })
  renderSearch()
  const user = await type('tiny')
  await screen.findByText('Tiny Tale (2019)')

  await user.click(screen.getByRole('button', { name: 'Ask for Tiny Tale' }))

  expect(
    await screen.findByText('You already asked for this one.'),
  ).toBeTruthy()
})

it('shows a kind message when a request is turned down and lets the kid retry', async () => {
  requestReply = async () =>
    Response.json({ title: "That one isn't available." }, { status: 422 })
  renderSearch()
  const user = await type('tiny')
  await screen.findByText('Tiny Tale (2019)')

  await user.click(screen.getByRole('button', { name: 'Ask for Tiny Tale' }))

  expect(await screen.findByText('That one is not available.')).toBeTruthy()
  expect(
    screen.getByRole('button', { name: 'Ask for Tiny Tale' }),
  ).toHaveProperty('disabled', false)
})

it('can be used from the keyboard alone', async () => {
  renderSearch()
  const user = userEvent.setup()
  await user.tab()
  expect(document.activeElement).toBe(screen.getByRole('searchbox'))
  await user.keyboard('tiny')
  await screen.findByText('Tiny Tale (2019)')

  await user.tab()
  expect(document.activeElement).toBe(
    screen.getByRole('link', { name: 'Tiny Tale (2019)' }),
  )
  await user.tab()
  expect(document.activeElement).toBe(
    screen.getByRole('button', { name: 'Ask for Tiny Tale' }),
  )
  await user.keyboard('{Enter}')
  expect(
    await screen.findByText('Asked! A parent will take a look.'),
  ).toBeTruthy()

  await user.click(screen.getByRole('searchbox'))
  await user.keyboard('{Escape}')
  expect((screen.getByRole('searchbox') as HTMLInputElement).value).toBe('')
  expect(screen.queryByText('Tiny Tale (2019)')).toBeNull()
})

it('works with touch taps', async () => {
  renderSearch()
  const user = userEvent.setup()
  await user.pointer({
    keys: '[TouchA]',
    target: screen.getByRole('searchbox'),
  })
  await user.keyboard('tiny')
  await screen.findByText('Tiny Tale (2019)')

  await user.pointer({
    keys: '[TouchA]',
    target: screen.getByRole('button', { name: 'Ask for Tiny Tale' }),
  })

  expect(
    await screen.findByText('Asked! A parent will take a look.'),
  ).toBeTruthy()
})

it('loads more results without repeating titles', async () => {
  searchReply = async (call) =>
    call.path.endsWith('page=2')
      ? page([tiny, bluey], false, 2)
      : page([tiny], true)
  renderSearch()
  const user = await type('tiny')
  await screen.findByText('Tiny Tale (2019)')

  await user.click(screen.getByRole('button', { name: 'Show more' }))

  expect(await screen.findByText('Bluey (2018)')).toBeTruthy()
  expect(screen.getAllByText('Tiny Tale (2019)')).toHaveLength(1)
  expect(screen.queryByRole('button', { name: 'Show more' })).toBeNull()
})

it('reports a failed search and tries again on request', async () => {
  let fail = true
  searchReply = async () =>
    fail ? Response.json({ title: 'x' }, { status: 502 }) : page([tiny])
  renderSearch()
  const user = await type('tiny')
  expect((await screen.findByRole('alert')).textContent).toContain(
    'Search is not working right now.',
  )

  fail = false
  await user.click(screen.getByRole('button', { name: 'Try again' }))

  expect(await screen.findByText('Tiny Tale (2019)')).toBeTruthy()
})

it('shows a masked title without poster or overview and still lets the kid ask', async () => {
  searchReply = async () => page([masked])
  const { container } = renderSearch()
  const user = await type('unrated')

  await screen.findByText('Unrated Short (2020)')
  expect(screen.getByText('Ask a parent')).toBeTruthy()
  expect(container.querySelector('img')).toBeNull()
  expect(container.querySelector('.result-overview')).toBeNull()
  expect(await axe(container)).toHaveNoViolations()

  await user.click(
    screen.getByRole('button', { name: 'Ask for Unrated Short' }),
  )

  expect(
    await screen.findByText('Asked! A parent will take a look.'),
  ).toBeTruthy()
  const made = calls.find((call) => call.path === '/api/v1/kid/requests')
  expect(made?.body).toEqual({ kind: 'movie', tmdbId: 802 })
})

it('does not mark ordinary titles as needing a parent', async () => {
  renderSearch()
  await type('tiny')

  await screen.findByText('Tiny Tale (2019)')
  expect(screen.queryByText('Ask a parent')).toBeNull()
})

it('keeps looking on the next page instead of calling an empty first page nothing found', async () => {
  searchReply = async (call) =>
    call.path.endsWith('page=1')
      ? page([], true, 1, queryOf(call))
      : call.path.endsWith('page=2')
        ? page([], true, 2, queryOf(call))
        : page([tiny], false, 3, queryOf(call))
  renderSearch()
  await type('tiny')

  expect(await screen.findByText('Tiny Tale (2019)')).toBeTruthy()
  expect(searches().map((call) => call.path)).toEqual([
    '/api/v1/kid/search?q=tiny&page=1',
    '/api/v1/kid/search?q=tiny&page=2',
    '/api/v1/kid/search?q=tiny&page=3',
  ])
  expect(screen.queryByText(/could not find anything/)).toBeNull()
})

it('stops after the last page and then says nothing was found', async () => {
  searchReply = async (call) => {
    const number = Number(
      new URL(call.path, 'http://x').searchParams.get('page'),
    )
    return page([], number < searchMaxPage, number, queryOf(call))
  }
  renderSearch()
  await type('zzzz')

  expect(
    await screen.findByText(/We could not find anything for "zzzz"/),
  ).toBeTruthy()
  expect(searches()).toHaveLength(searchMaxPage)
  expect(screen.queryByRole('button', { name: 'Show more' })).toBeNull()
})

it('words the empty state with the searched text, not what is typed since', async () => {
  searchReply = async (call) =>
    queryOf(call) === 'zzzz'
      ? page([], false, 1, 'zzzz')
      : new Promise<Response>(() => undefined)
  renderSearch()
  const user = await type('zzzz')
  await screen.findByText(/We could not find anything for "zzzz"/)

  await user.type(screen.getByRole('searchbox'), 'y')

  expect(screen.getByText(/We could not find anything for "zzzz"/)).toBeTruthy()
})

it('uses the singular when exactly one title is found', async () => {
  searchReply = async () => page([tiny])
  renderSearch()
  await type('tiny')

  expect(await screen.findByText('Found 1 title')).toBeTruthy()
})

it('keeps loaded results when show more fails and retries only that page', async () => {
  let fail = true
  searchReply = async (call) =>
    call.path.endsWith('page=2')
      ? fail
        ? Response.json({ title: 'x' }, { status: 502 })
        : page([bluey], false, 2)
      : page([tiny], true)
  renderSearch()
  const user = await type('tiny')
  await screen.findByText('Tiny Tale (2019)')

  await user.click(screen.getByRole('button', { name: 'Show more' }))

  expect((await screen.findByRole('alert')).textContent).toContain(
    'We could not load more titles.',
  )
  expect(screen.getByText('Tiny Tale (2019)')).toBeTruthy()

  fail = false
  await user.click(screen.getByRole('button', { name: 'Try again' }))

  expect(await screen.findByText('Bluey (2018)')).toBeTruthy()
  expect(screen.getByText('Tiny Tale (2019)')).toBeTruthy()
  expect(screen.queryByRole('alert')).toBeNull()
  expect(searches().map((call) => call.path)).toEqual([
    '/api/v1/kid/search?q=tiny&page=1',
    '/api/v1/kid/search?q=tiny&page=2',
    '/api/v1/kid/search?q=tiny&page=2',
  ])
})

it('keeps the accessible name while a request is being sent', async () => {
  requestReply = () => new Promise<Response>(() => undefined)
  renderSearch()
  const user = await type('tiny')
  await screen.findByText('Tiny Tale (2019)')

  await user.click(screen.getByRole('button', { name: 'Ask for Tiny Tale' }))

  const button = await screen.findByRole('button', {
    name: 'Ask for Tiny Tale',
    description: undefined,
  })
  expect(button.textContent).toBe('Asking')
  expect(button).toHaveProperty('disabled', true)
})
