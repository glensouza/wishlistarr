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
  request: {
    state: string
    requestCount: number
    seasons?: {
      number: number
      availability: string
      availableEpisodes: number
      requested: boolean
    }[]
  } | null
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

it('moves between the back link and the trailer with the arrow keys', async () => {
  renderAt('/title/movie/800')
  const back = await screen.findByRole('link', { name: 'Back' })
  const play = screen.getByRole('button', { name: 'Play trailer' })
  const ask = screen.getByRole('button', { name: 'Ask' })
  place(back, 0, 0)
  place(ask, 320, 0)
  place(play, 0, 140)
  back.focus()
  back.dispatchEvent(
    new KeyboardEvent('keydown', {
      key: 'ArrowDown',
      bubbles: true,
      cancelable: true,
    }),
  )
  expect(document.activeElement).toBe(play)
  play.dispatchEvent(
    new KeyboardEvent('keydown', {
      key: 'ArrowUp',
      bubbles: true,
      cancelable: true,
    }),
  )
  expect(document.activeElement).toBe(back)
})

it('moves between the season choices and ask with the arrow keys', async () => {
  payload = structuredClone(show)
  renderAt('/title/show/82728')
  const ask = await screen.findByRole('button', { name: 'Ask' })
  const back = screen.getByRole('link', { name: 'Back' })
  const specials = screen.getByRole('radio', {
    name: 'Specials',
  }) as HTMLInputElement
  const season = screen.getByRole('radio', {
    name: 'Season 1',
  }) as HTMLInputElement
  const puppy = screen.getByRole('radio', {
    name: 'The puppy year',
  }) as HTMLInputElement
  place(back, 0, 0, 96, 40)
  place(ask, 0, 120, 180, 56)
  place(specials, 0, 240, 168, 44)
  place(season, 188, 252, 168, 44)
  place(puppy, 376, 236, 180, 44)

  expect(season.checked).toBe(true)
  expect(specials.checked).toBe(false)
  ask.focus()
  const down = new KeyboardEvent('keydown', {
    key: 'ArrowDown',
    bubbles: true,
    cancelable: true,
  })
  ask.dispatchEvent(down)
  expect(down.defaultPrevented).toBe(true)
  expect(document.activeElement).toBe(specials)
  expect(specials.checked).toBe(false)
  expect(season.checked).toBe(true)

  specials.dispatchEvent(
    new KeyboardEvent('keydown', {
      key: 'ArrowRight',
      bubbles: true,
      cancelable: true,
    }),
  )
  expect(document.activeElement).toBe(season)
  expect(season.checked).toBe(true)
  season.dispatchEvent(
    new KeyboardEvent('keydown', {
      key: 'ArrowRight',
      bubbles: true,
      cancelable: true,
    }),
  )
  expect(document.activeElement).toBe(puppy)
  expect(puppy.checked).toBe(false)
  puppy.dispatchEvent(
    new KeyboardEvent('keydown', {
      key: 'Enter',
      bubbles: true,
      cancelable: true,
    }),
  )
  expect(puppy.checked).toBe(true)
  expect(season.checked).toBe(false)

  const edge = new KeyboardEvent('keydown', {
    key: 'ArrowDown',
    bubbles: true,
    cancelable: true,
  })
  season.focus()
  season.dispatchEvent(edge)
  expect(edge.defaultPrevented).toBe(true)
  expect(document.activeElement).toBe(season)
})

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
  ['available', 'Almost ready'],
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

it('sends the chosen season with a show request', async () => {
  payload = structuredClone(show)
  const user = userEvent.setup()
  renderAt('/title/show/82728')

  expect(
    await screen.findByText(
      'Choose a season to request. Access in Plex is for the whole show, including future seasons.',
    ),
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
  expect(requests()[0].body).toEqual({
    kind: 'show',
    tmdbId: 82728,
    seasons: [0],
  })
})

it('still explains show access when a show has no seasons', async () => {
  payload = { ...show, seasons: [] }
  renderAt('/title/show/82728')

  expect(
    await screen.findByText(
      'Choose a season to request. Access in Plex is for the whole show, including future seasons.',
    ),
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

it('does not use the same words for the library badge and an almost-ready request', async () => {
  payload = {
    ...movie,
    availability: 'inLibrary',
    request: { state: 'available', requestCount: 1 },
  }
  renderAt('/title/movie/800')

  expect(await screen.findByText('Ready to watch')).toBeTruthy()
  expect(screen.getAllByText('Ready to watch')).toHaveLength(1)
  expect(screen.getByRole('button', { name: 'Almost ready' })).toBeTruthy()
})

it('shows partial season availability and lets an ongoing show request a newly announced season', async () => {
  payload = {
    ...show,
    request: {
      state: 'monitoring',
      requestCount: 1,
      seasons: [
        {
          number: 1,
          availability: 'partial',
          availableEpisodes: 3,
          requested: true,
        },
        {
          number: 2,
          availability: 'missing',
          availableEpisodes: 0,
          requested: false,
        },
      ],
    },
  } as typeof payload
  const user = userEvent.setup()
  renderAt('/title/show/82728')
  expect(await screen.findByText('Some episodes are ready')).toBeTruthy()
  expect(
    (screen.getByRole('button', { name: 'Ask' }) as HTMLButtonElement).disabled,
  ).toBe(true)
  await user.click(screen.getByRole('radio', { name: 'The puppy year' }))
  expect(screen.getByRole('status').textContent).toBe('Not here yet')
  await user.click(screen.getByRole('button', { name: 'Ask' }))
  expect(requests()[0].body).toEqual({
    kind: 'show',
    tmdbId: 82728,
    seasons: [2],
  })
})

it('explains when a selected season is no longer listed', async () => {
  payload = structuredClone(show)
  requestStatus = 400
  const user = userEvent.setup()
  renderAt('/title/show/82728')
  await user.click(await screen.findByRole('button', { name: 'Ask' }))
  expect(await screen.findByRole('alert')).toHaveProperty(
    'textContent',
    'That season is no longer listed. Refresh this page and choose another.',
  )
  expect(
    (screen.getByRole('button', { name: 'Ask' }) as HTMLButtonElement).disabled,
  ).toBe(false)
})
