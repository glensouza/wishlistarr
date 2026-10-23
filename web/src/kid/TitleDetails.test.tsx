import { cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { axe } from '../test/axe'
import { KidSessionProvider } from './KidSession'
import TitleDetails from './TitleDetails'
import { youtubeApiUrl } from './Trailer'

type Cast = {
  name: string
  character: string | null
  profileUrl: string | null
}

type Season = {
  number: number
  name: string | null
  overview: string | null
  posterUrl: string | null
  episodeCount: number
  year: string | null
}

type Payload = {
  kind: 'movie' | 'show'
  tmdbId: number
  title: string
  year: string | null
  overview: string | null
  posterUrl: string | null
  backdropUrl: string | null
  runtimeMinutes: number | null
  rating: string | null
  availability: 'inLibrary' | 'notInLibrary' | null
  needsParent: boolean
  trailerKey: string | null
  cast: Cast[]
  seasons: Season[]
  request: { state: string; requestCount: number } | null
}

type Call = { method: string; path: string; body: unknown }

const movie: Payload = {
  kind: 'movie',
  tmdbId: 800,
  title: 'Tiny Tale',
  year: '2019',
  overview: 'A gentle story about a fox.',
  posterUrl: '/api/v1/kid/images/tmdb/w342/tiny.jpg',
  backdropUrl: '/api/v1/kid/images/tmdb/w1280/tiny-bg.jpg',
  runtimeMinutes: 95,
  rating: 'PG',
  availability: 'inLibrary',
  needsParent: false,
  trailerKey: 'M7lc1UVf-VE',
  cast: [
    {
      name: 'Ada Lovelace',
      character: 'Fox',
      profileUrl: '/api/v1/kid/images/tmdb/w185/ada.jpg',
    },
  ],
  seasons: [],
  request: null,
}

const show: Payload = {
  kind: 'show',
  tmdbId: 82728,
  title: 'Bluey',
  year: '2018',
  overview: 'A family of dogs.',
  posterUrl: null,
  backdropUrl: null,
  runtimeMinutes: 7,
  rating: 'TV-Y',
  availability: 'notInLibrary',
  needsParent: false,
  trailerKey: null,
  cast: [],
  seasons: [
    {
      number: 0,
      name: null,
      overview: 'Extras.',
      posterUrl: null,
      episodeCount: 2,
      year: null,
    },
    {
      number: 1,
      name: null,
      overview: 'The first year.',
      posterUrl: null,
      episodeCount: 10,
      year: '2018',
    },
    {
      number: 2,
      name: 'The puppy year',
      overview: 'A puppy arrives.',
      posterUrl: '/api/v1/kid/images/tmdb/w185/puppy.jpg',
      episodeCount: 1,
      year: '2019',
    },
  ],
  request: null,
}

let payload: Payload
let titleStatus: number
let requestStatus: number
let requestState: string
let signedIn: boolean
let calls: Call[]

beforeEach(() => {
  payload = structuredClone(movie)
  titleStatus = 200
  requestStatus = 201
  requestState = 'pending'
  signedIn = true
  calls = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (path: string, init?: RequestInit) => {
      if (path === '/api/v1/auth/antiforgery') {
        return Response.json({ token: 'tok', headerName: 'X-CSRF-TOKEN' })
      }
      if (path === '/api/v1/kid/me') {
        return signedIn
          ? Response.json({ id: '1', name: 'Ada' })
          : Response.json({ title: 'Sign in first.' }, { status: 401 })
      }
      const call: Call = {
        method: init?.method ?? 'GET',
        path,
        body: init?.body ? JSON.parse(init.body as string) : undefined,
      }
      calls.push(call)
      if (path.startsWith('/api/v1/kid/titles/')) {
        return titleStatus === 200
          ? Response.json(payload)
          : Response.json({ title: 'hidden' }, { status: titleStatus })
      }
      return Response.json(
        { id: 4, state: requestState, requestCount: 1 },
        { status: requestStatus },
      )
    }),
  )
})

afterEach(() => {
  cleanup()
  document
    .querySelectorAll('script')
    .forEach((script) => script.src === youtubeApiUrl && script.remove())
  delete window.YTConfig
  delete window.onYouTubeIframeAPIReady
  vi.unstubAllGlobals()
})

function renderAt(path: string) {
  return render(
    <KidSessionProvider>
      <MemoryRouter initialEntries={[path]}>
        <Routes>
          <Route path="/" element={<h1>Picker</h1>} />
          <Route path="title/:kind/:tmdbId" element={<TitleDetails />} />
        </Routes>
      </MemoryRouter>
    </KidSessionProvider>,
  )
}

const requests = () =>
  calls.filter((call) => call.path === '/api/v1/kid/requests')

it('shows the title and loads a trailer only after play is clicked', async () => {
  const user = userEvent.setup()
  const { container } = renderAt('/title/movie/800')

  expect(
    await screen.findByRole('heading', { name: 'Tiny Tale (2019)' }),
  ).toBeTruthy()
  expect(screen.getByText('A gentle story about a fox.')).toBeTruthy()
  expect(screen.getByText('Ada Lovelace')).toBeTruthy()
  expect(screen.getByText('Fox')).toBeTruthy()
  expect(screen.getByText('PG')).toBeTruthy()
  expect(screen.getByText('95 min')).toBeTruthy()
  expect(screen.getByText('Ready to watch')).toBeTruthy()
  expect(container.innerHTML).not.toMatch(/youtube|ytimg/i)
  expect(
    [...document.scripts].some((script) => script.src === youtubeApiUrl),
  ).toBe(false)
  expect(document.querySelector('iframe')).toBeNull()

  await user.click(screen.getByRole('button', { name: 'Play trailer' }))

  expect(
    [...document.scripts].some((script) => script.src === youtubeApiUrl),
  ).toBe(true)
  expect(window.YTConfig?.host).toBe('https://www.youtube-nocookie.com')
  expect(document.querySelector('iframe')).toBeNull()
  expect(await axe(container)).toHaveNoViolations()
})

it('renders no trailer when the title has no key', async () => {
  payload = { ...movie, trailerKey: null, cast: [] }
  const { container } = renderAt('/title/movie/800')

  await screen.findByRole('heading', { name: 'Tiny Tale (2019)' })
  expect(screen.queryByRole('button', { name: 'Play trailer' })).toBeNull()
  expect(container.innerHTML).not.toMatch(/youtube|ytimg/i)
  expect(
    [...document.scripts].some((script) => script.src === youtubeApiUrl),
  ).toBe(false)
})

it('asks for a title and then shows that the request is pending', async () => {
  const user = userEvent.setup()
  renderAt('/title/movie/800')

  await user.click(await screen.findByRole('button', { name: 'Ask' }))

  const button = await screen.findByRole('button', { name: 'Asked' })
  expect((button as HTMLButtonElement).disabled).toBe(true)
  expect(requests()).toEqual([
    {
      method: 'POST',
      path: '/api/v1/kid/requests',
      body: { kind: 'movie', tmdbId: 800 },
    },
  ])
  await user.click(button)
  expect(requests()).toHaveLength(1)
})

it.each([
  ['pending', 'Asked'],
  ['approved', 'On the way'],
  ['fulfilling', 'On the way'],
  ['available', 'Ready to watch'],
  ['labeled', 'Added'],
  ['failed', 'Could not add it'],
  ['elsewhere', 'Could not add it'],
])('shows a %s request as %s and does not ask again', async (state, label) => {
  payload = {
    ...movie,
    availability: null,
    request: { state, requestCount: 1 },
  }
  const user = userEvent.setup()
  renderAt('/title/movie/800')

  const button = (await screen.findByRole('button', {
    name: label,
  })) as HTMLButtonElement
  expect(button.disabled).toBe(true)
  await user.click(button)
  expect(requests()).toHaveLength(0)
})

it('lets a kid ask again when the open request was denied', async () => {
  payload = { ...movie, request: { state: 'denied', requestCount: 1 } }
  renderAt('/title/movie/800')

  expect(await screen.findByRole('button', { name: 'Ask' })).toBeTruthy()
})

it('keeps the season choice off the request', async () => {
  payload = structuredClone(show)
  const user = userEvent.setup()
  renderAt('/title/show/82728')

  expect(
    await screen.findByText('Access is for the whole show, not one season.'),
  ).toBeTruthy()
  expect(screen.getByText('The first year.')).toBeTruthy()
  expect(screen.getByText('10 episodes')).toBeTruthy()
  expect(screen.getByText('7 min per episode')).toBeTruthy()
  const radios = screen.getAllByRole('radio')
  expect(radios).toHaveLength(3)
  expect(new Set(radios.map((radio) => radio.getAttribute('name'))).size).toBe(
    1,
  )
  expect(
    (screen.getByRole('radio', { name: 'Season 1' }) as HTMLInputElement)
      .checked,
  ).toBe(true)

  await user.click(screen.getByRole('radio', { name: 'The puppy year' }))
  expect(screen.getByText('A puppy arrives.')).toBeTruthy()
  expect(screen.getByText('1 episode')).toBeTruthy()
  expect(screen.queryByText('The first year.')).toBeNull()

  await user.click(screen.getByRole('radio', { name: 'Specials' }))
  expect(screen.getByText('Extras.')).toBeTruthy()
  expect(screen.getByText('2 episodes')).toBeTruthy()
  expect(requests()).toHaveLength(0)

  await user.click(screen.getByRole('button', { name: 'Ask' }))
  await screen.findByRole('button', { name: 'Asked' })
  expect(requests()[0].body).toEqual({ kind: 'show', tmdbId: 82728 })
})

it('still explains show access when a show has no seasons', async () => {
  payload = { ...show, seasons: [] }
  renderAt('/title/show/82728')

  expect(
    await screen.findByText('Access is for the whole show, not one season.'),
  ).toBeTruthy()
  expect(screen.queryByRole('radio')).toBeNull()
})

it('uses a plain block when artwork is missing', async () => {
  payload = {
    ...movie,
    posterUrl: null,
    backdropUrl: null,
    cast: [],
    trailerKey: null,
  }
  const { container } = renderAt('/title/movie/800')

  await screen.findByRole('heading', { name: 'Tiny Tale (2019)' })
  expect(container.querySelector('img')).toBeNull()
  expect(container.querySelector('.artwork-fallback')).toBeTruthy()
})

it('replaces artwork that fails to load', async () => {
  const { container } = renderAt('/title/movie/800')
  await screen.findByRole('heading', { name: 'Tiny Tale (2019)' })
  const images = [...container.querySelectorAll('img')]
  expect(images.length).toBeGreaterThan(0)

  for (const image of images) {
    image.dispatchEvent(new Event('error'))
  }

  await waitFor(() => expect(container.querySelector('img')).toBeNull())
  expect(container.querySelector('.artwork-fallback')).toBeTruthy()
})

it('hides a masked title story and does not offer a trailer', async () => {
  payload = {
    ...movie,
    overview: null,
    posterUrl: null,
    backdropUrl: null,
    trailerKey: null,
    cast: [],
    needsParent: true,
  }
  const { container } = renderAt('/title/movie/800')

  await screen.findByRole('heading', { name: 'Tiny Tale (2019)' })
  expect(screen.getByText('Ask a parent')).toBeTruthy()
  expect(screen.queryByText('A gentle story about a fox.')).toBeNull()
  expect(screen.queryByRole('button', { name: 'Play trailer' })).toBeNull()
  expect(container.innerHTML).not.toMatch(/youtube|ytimg/i)
  expect(container.querySelector('img')).toBeNull()
})

it('does not look up a route the screen cannot show', async () => {
  renderAt('/title/person/800')

  expect(
    await screen.findByRole('heading', { name: "We couldn't find that one." }),
  ).toBeTruthy()
  expect(calls.some((call) => call.path.includes('/titles/'))).toBe(false)
})

it('treats a missing title as not found', async () => {
  titleStatus = 404
  renderAt('/title/movie/800')

  expect(
    await screen.findByRole('heading', { name: "We couldn't find that one." }),
  ).toBeTruthy()
})

it('lets the kid try a failed page again', async () => {
  titleStatus = 502
  const user = userEvent.setup()
  renderAt('/title/movie/800')

  expect(await screen.findByRole('alert')).toBeTruthy()
  titleStatus = 200
  await user.click(screen.getByRole('button', { name: 'Try again' }))
  expect(
    await screen.findByRole('heading', { name: 'Tiny Tale (2019)' }),
  ).toBeTruthy()
})

it('sends a signed-out kid back to the picker', async () => {
  signedIn = false
  renderAt('/title/movie/800')

  expect(await screen.findByRole('heading', { name: 'Picker' })).toBeTruthy()
  expect(calls.some((call) => call.path.includes('/titles/'))).toBe(false)
})
