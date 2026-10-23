import { cleanup, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, expect, it, vi } from 'vitest'
import App from '../App'
import { axe } from '../test/axe'

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

const notice =
  'This product uses the TMDB API but is not endorsed or certified by TMDB.'
const npmPackages = {
  packages: [{ name: 'zeta-pkg', version: '2.0.0', license: 'MIT' }],
}
const nugetPackages = {
  packages: [{ name: 'alpha-pkg', version: '1.0.0', license: 'Apache-2.0' }],
}

function renderAt(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <App />
    </MemoryRouter>,
  )
}

function jsonResponse(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  })
}

function stubLicenses(npm: unknown, nuget: unknown) {
  const calls: string[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (path: string) => {
      calls.push(path)
      if (path === '/api/v1/auth/status') {
        return jsonResponse({
          setupRequired: false,
          principalType: null,
          kidDevice: { name: 'Tablet' },
        })
      }
      if (path === '/licenses/npm.json') return respond(npm)
      if (path === '/licenses/nuget.json') return respond(nuget)
      return jsonResponse({ title: 'Nope' }, 401)
    }),
  )
  return calls
}

function respond(body: unknown) {
  if (body instanceof Response) return body
  if (body === null) return jsonResponse({ title: 'Missing' }, 404)
  return jsonResponse(body)
}

it('shows the TMDB notice, logo, and the other credits', async () => {
  const calls = stubLicenses(npmPackages, nugetPackages)
  const { container } = renderAt('/about')

  expect(await screen.findByRole('heading', { name: 'About' })).toBeTruthy()
  expect(screen.getByText(notice)).toBeTruthy()
  const logo = screen.getByRole('img', { name: 'TMDB' })
  const src = logo.getAttribute('src') ?? ''
  expect(src).toContain('273.42')
  expect(src).toContain('90cea1')
  expect(src).toContain('00b3e5')
  expect(logo.closest('a')?.getAttribute('href')).toBe(
    'https://www.themoviedb.org/',
  )
  expect(
    screen.getByRole('link', { name: 'JustWatch' }).getAttribute('href'),
  ).toBe('https://www.justwatch.com/')
  expect(screen.getByText(/Watch provider data from/).textContent).toBe(
    'Watch provider data from JustWatch',
  )
  expect(
    screen.getByRole('link', { name: 'fanart.tv' }).getAttribute('href'),
  ).toBe('https://fanart.tv/')
  const dog = screen.getByRole('link', { name: 'DoesTheDogDie.com' })
  expect(dog.getAttribute('href')).toBe('https://www.doesthedogdie.com')
  expect(dog.parentElement?.textContent).toBe('Powered by DoesTheDogDie.com')
  expect(
    screen.getByText(
      'Wishlistarr is not affiliated with, endorsed, or sponsored by Plex, Inc. Plex is a trademark of Plex, Inc.',
    ),
  ).toBeTruthy()
  expect(
    screen
      .getByRole('link', { name: 'GNU General Public License v3.0' })
      .getAttribute('href'),
  ).toBe('https://www.gnu.org/licenses/gpl-3.0.html')

  const items = await screen.findAllByRole('listitem')
  expect(items.map((item) => item.textContent)).toEqual([
    'alpha-pkg 1.0.0 (Apache-2.0)',
    'zeta-pkg 2.0.0 (MIT)',
  ])
  expect(calls.filter((path) => path.startsWith('/api/')).sort()).toEqual([
    '/api/v1/auth/status',
    '/api/v1/kid/me',
  ])
  expect(await axe(container)).toHaveNoViolations()
})

it('does not list packages when either notice file fails', async () => {
  stubLicenses(npmPackages, null)
  renderAt('/about')
  expect(
    await screen.findByText('The package license list is unavailable.'),
  ).toBeTruthy()
  expect(screen.queryByText(/zeta-pkg/)).toBeNull()
  expect(screen.queryByText(/alpha-pkg/)).toBeNull()
})

it('does not treat the site shell or a partial file as a license list', async () => {
  stubLicenses(
    new Response('<!doctype html><title>test-shell</title>', {
      status: 200,
      headers: { 'content-type': 'text/html' },
    }),
    { packages: [{ name: 'hidden-pkg', version: '9', license: 'MIT' }] },
  )
  renderAt('/about')
  expect(
    await screen.findByText('The package license list is unavailable.'),
  ).toBeTruthy()
  expect(screen.queryByText(/hidden-pkg/)).toBeNull()
  expect(screen.getByText(notice)).toBeTruthy()
})

it('rejects an empty notice file', async () => {
  stubLicenses({ packages: [] }, nugetPackages)
  renderAt('/about')
  expect(
    await screen.findByText('The package license list is unavailable.'),
  ).toBeTruthy()
  expect(screen.queryByText(/alpha-pkg/)).toBeNull()
})

it('opens About from the footer without leaving kid mode', async () => {
  const user = userEvent.setup()
  stubLicenses(npmPackages, nugetPackages)
  renderAt('/')
  await user.click(screen.getByRole('link', { name: 'About' }))
  expect(await screen.findByText(notice)).toBeTruthy()
  expect(screen.queryByRole('heading', { name: 'Parents' })).toBeNull()
})
