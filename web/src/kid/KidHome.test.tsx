import { readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from '@testing-library/react'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { MemoryRouter } from 'react-router-dom'
import { axe } from '../test/axe'
import KidHome from './KidHome'
import { KidSessionProvider } from './KidSession'

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

const rows = [
  { id: 'popular', genreId: null, genreName: null, items: [tiny, bluey] },
  { id: 'genre:16', genreId: 16, genreName: 'Animation', items: [tiny] },
  { id: 'genre:99', genreId: 99, genreName: 'Mystery', items: [] },
  { id: 'genre:37', genreId: 37, genreName: 'Western', items: [bluey] },
  { id: 'genre:12', genreId: 12, genreName: null, items: [bluey] },
]

let homeStatus = 200
let homeBody: unknown = { rows }
let requests: {
  path: string
  method: string
  body: unknown
  headers: Headers
}[]

beforeEach(() => {
  homeStatus = 200
  homeBody = { rows }
  requests = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (path: string, init?: RequestInit) => {
      const method = init?.method ?? 'GET'
      if (path === '/api/v1/auth/antiforgery') {
        return Response.json({ token: 'tok', headerName: 'X-CSRF-TOKEN' })
      }
      if (path === '/api/v1/kid/me') {
        return Response.json({ id: '1', name: 'Ada' })
      }
      if (path === '/api/v1/kid/signout') {
        return new Response(null, { status: 204 })
      }
      requests.push({
        path,
        method,
        body: init?.body ? JSON.parse(init.body as string) : undefined,
        headers: new Headers(init?.headers),
      })
      if (path === '/api/v1/kid/home') {
        return Response.json(homeBody, { status: homeStatus })
      }
      return Response.json(
        { id: 1, state: 'pending', requestCount: 1 },
        { status: 201 },
      )
    }),
  )
})

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

function renderHome() {
  return render(
    <MemoryRouter>
      <KidSessionProvider>
        <KidHome />
      </KidSessionProvider>
    </MemoryRouter>,
  )
}

it('shows allowed rows with lazy posters, a reserved frame and a library badge', async () => {
  const { container } = renderHome()

  expect(
    await screen.findByRole('heading', { name: 'Popular for my age' }),
  ).toBeTruthy()
  expect(screen.getByRole('heading', { name: 'Animation' })).toBeTruthy()
  expect(screen.getByRole('heading', { name: 'Western' })).toBeTruthy()
  expect(screen.getByRole('heading', { name: 'Genre 12' })).toBeTruthy()
  expect(screen.queryByRole('heading', { name: 'Mystery' })).toBeNull()
  expect(
    screen.getByText('Here are some ideas, or search for something else.'),
  ).toBeTruthy()

  const poster = container.querySelector('img')
  expect(poster?.getAttribute('src')).toBe(
    '/api/v1/kid/images/tmdb/w342/tiny.jpg',
  )
  expect(poster?.getAttribute('loading')).toBe('lazy')
  expect(poster?.getAttribute('alt')).toBe('')
  expect(poster?.closest('.poster-frame')).toBeTruthy()
  expect(screen.queryAllByRole('img').length).toBe(0)
  expect(
    screen.getAllByRole('heading', { name: 'Bluey' }).length,
  ).toBeGreaterThan(0)
  expect(
    screen.getAllByText('Tiny Tale is ready to watch').length,
  ).toBeGreaterThan(0)
  expect(screen.queryByText('Bluey is ready to watch')).toBeNull()

  const css = readFileSync(
    join(dirname(fileURLToPath(import.meta.url)), '../styles.css'),
    'utf8',
  )
  expect(css).toMatch(/\.poster-frame\s*\{[^}]*aspect-ratio:\s*2\s*\/\s*3/)

  fireEvent.error(poster!)
  expect(screen.queryAllByRole('img').length).toBe(0)

  expect(await axe(container)).toHaveNoViolations()
})

it('moves focus across a row and between rows with the arrow keys', async () => {
  renderHome()
  const first = (
    await screen.findAllByRole('button', { name: 'Ask for Tiny Tale' })
  )[0]
  const second = screen.getAllByRole('button', { name: 'Ask for Bluey' })[0]
  first.focus()
  fireEvent.keyDown(first, { key: 'ArrowRight' })
  expect(document.activeElement).toBe(second)
  fireEvent.keyDown(second, { key: 'ArrowDown' })
  const animationAsk = screen.getAllByRole('button', {
    name: 'Ask for Tiny Tale',
  })[1]
  expect(document.activeElement).toBe(animationAsk)
  fireEvent.keyDown(animationAsk, { key: 'ArrowLeft' })
  expect(document.activeElement).toBe(animationAsk)
})

it('asks for a title from a home row', async () => {
  renderHome()
  fireEvent.click(
    (await screen.findAllByRole('button', { name: 'Ask for Tiny Tale' }))[0],
  )
  await waitFor(() =>
    expect(requests.some((call) => call.path === '/api/v1/kid/requests')).toBe(
      true,
    ),
  )
  const posted = requests.find((call) => call.path === '/api/v1/kid/requests')
  expect(posted?.method).toBe('POST')
  expect(posted?.body).toEqual({ kind: 'movie', tmdbId: 800 })
  expect(posted?.headers.get('X-CSRF-TOKEN')).toBe('tok')
  expect(
    await screen.findByText('Asked! A parent will take a look.'),
  ).toBeTruthy()
})

it('retries when the rows cannot be loaded', async () => {
  homeStatus = 502
  homeBody = { title: 'Home is not working right now.' }
  renderHome()
  expect(
    await screen.findByText(
      'These rows are not loading right now. Try again in a little while.',
    ),
  ).toBeTruthy()
  homeStatus = 200
  homeBody = { rows }
  fireEvent.click(screen.getByRole('button', { name: 'Try again' }))
  expect(
    await screen.findByRole('heading', { name: 'Popular for my age' }),
  ).toBeTruthy()
})

it('says when there is nothing to show', async () => {
  homeBody = { rows: [] }
  renderHome()
  expect(
    await screen.findByText('Nothing to show here yet. Try a search.'),
  ).toBeTruthy()
})
