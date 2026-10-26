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
  'This product uses TMDB and the TMDB APIs but is not endorsed, certified, or otherwise approved by TMDB.'
const npmPackages = {
  packages: [{ name: 'zeta-pkg', version: '2.0.0', license: 'MIT' }],
}
const nugetPackages = {
  packages: [{ name: 'alpha-pkg', version: '1.0.0', license: 'Apache-2.0' }],
}
const parent = {
  setupRequired: false,
  principalType: 'Parent',
  kidDevice: null,
}
const pairedDevice = {
  setupRequired: false,
  principalType: null,
  kidDevice: { name: 'Tablet' },
}
const pairedKid = { ...pairedDevice, principalType: 'Kid' }

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

function respond(body: unknown) {
  if (body instanceof Response) return body
  if (body === null) return jsonResponse({ title: 'Missing' }, 404)
  return jsonResponse(body)
}

function stubApp(
  status: unknown,
  npm: unknown = npmPackages,
  nuget: unknown = nugetPackages,
) {
  const calls: string[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (path: string) => {
      calls.push(path)
      if (path === '/api/v1/auth/status') return jsonResponse(status)
      if (path === '/api/v1/kid/profiles') return jsonResponse([])
      if (path === '/licenses/npm.json') return respond(npm)
      if (path === '/licenses/nuget.json') return respond(nuget)
      if (path.startsWith('/api/v1/parent') || path === '/api/v1/kid/me') {
        return jsonResponse([])
      }
      return jsonResponse({ title: 'Nope' }, 401)
    }),
  )
  return calls
}

function expectNoOutboundLinks(container: HTMLElement) {
  expect(
    container.querySelectorAll('a[href^="http"], a[href^="//"]'),
  ).toHaveLength(0)
  expect(container.querySelectorAll('a[target]')).toHaveLength(0)
  expect(screen.queryByRole('link', { name: 'About' })).toBeNull()
  expect(screen.queryByText(/DoesTheDogDie/)).toBeNull()
  expect(screen.queryByText(/fanart\.tv/)).toBeNull()
}

function expectPlainTmdbCredit() {
  expect(screen.getByText(notice)).toBeTruthy()
  const logo = screen.getByRole('img', { name: 'TMDB' })
  expect(logo.closest('a')).toBeNull()
}

it('shows the TMDB notice, logo, and the other credits to a parent', async () => {
  const calls = stubApp(parent)
  const { container } = renderAt('/about')

  expect(await screen.findByRole('heading', { name: 'About' })).toBeTruthy()
  expect(screen.getAllByText(notice)).toHaveLength(2)
  const logos = screen.getAllByRole('img', { name: 'TMDB' })
  const linked = logos.filter((logo) => logo.closest('a'))
  expect(linked).toHaveLength(1)
  expect(linked[0].closest('a')?.getAttribute('href')).toBe(
    'https://www.themoviedb.org/',
  )

  const justWatch = screen.getByRole('link', { name: 'JustWatch' })
  expect(justWatch.getAttribute('href')).toBe('https://www.justwatch.com/')
  expect(justWatch.parentElement?.textContent).toBe(
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
  const gpl = screen.getByRole('link', {
    name: 'GNU General Public License v3.0',
  })
  expect(gpl.getAttribute('href')).toBe(
    'https://www.gnu.org/licenses/gpl-3.0.html',
  )

  const outbound = container.querySelectorAll('a[href^="https://"]')
  expect(outbound).toHaveLength(5)
  for (const anchor of outbound) {
    expect(anchor.getAttribute('target')).toBe('_blank')
    expect(anchor.getAttribute('rel')).toBe('noreferrer noopener')
  }

  const items = await screen.findAllByRole('listitem')
  expect(items.map((item) => item.textContent)).toEqual([
    'alpha-pkg 1.0.0 (Apache-2.0)',
    'zeta-pkg 2.0.0 (MIT)',
  ])
  expect(calls.some((path) => path.startsWith('/api/v1/parent'))).toBe(false)
  expect(calls).toContain('/api/v1/auth/status')
  expect(await axe(container)).toHaveNoViolations()
})

it('links to About from the parent area when signed in as a parent', async () => {
  const user = userEvent.setup()
  stubApp(parent)
  renderAt('/parent')
  await user.click(await screen.findByRole('link', { name: 'About' }))
  expect(await screen.findByRole('heading', { name: 'About' })).toBeTruthy()
})

it('does not list packages when either notice file fails', async () => {
  stubApp(parent, npmPackages, null)
  renderAt('/about')
  expect(
    await screen.findByText('The package license list is unavailable.'),
  ).toBeTruthy()
  expect(screen.queryByText(/zeta-pkg/)).toBeNull()
  expect(screen.queryByText(/alpha-pkg/)).toBeNull()
})

it('does not treat the site shell or a partial file as a license list', async () => {
  stubApp(
    parent,
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
  expect(screen.getAllByText(notice).length).toBeGreaterThan(0)
})

it('rejects an empty notice file', async () => {
  stubApp(parent, { packages: [] }, nugetPackages)
  renderAt('/about')
  expect(
    await screen.findByText('The package license list is unavailable.'),
  ).toBeTruthy()
  expect(screen.queryByText(/alpha-pkg/)).toBeNull()
})

it('shows the profile picker with the TMDB credit as plain text and no outbound links', async () => {
  stubApp(pairedDevice)
  const { container } = renderAt('/')
  await screen.findByText('No profiles yet. Ask a parent to add one.')
  expectPlainTmdbCredit()
  expectNoOutboundLinks(container)
  expect(await axe(container)).toHaveNoViolations()
})

it('shows the kid home with no outbound links', async () => {
  stubApp(pairedKid)
  const { container } = renderAt('/home')
  expect(await screen.findByRole('main')).toBeTruthy()
  await screen.findByRole('contentinfo')
  expectPlainTmdbCredit()
  expectNoOutboundLinks(container)
})

it('keeps a paired device parent sign-in free of outbound links', async () => {
  stubApp(pairedDevice)
  const { container } = renderAt('/parent')
  expect(
    await screen.findByRole('button', { name: 'Leave kid mode' }),
  ).toBeTruthy()
  expectPlainTmdbCredit()
  expectNoOutboundLinks(container)
})

it('sends a paired device that is not a parent away from About', async () => {
  const calls = stubApp(pairedDevice)
  const { container } = renderAt('/about')
  expect(
    await screen.findByRole('button', { name: 'Leave kid mode' }),
  ).toBeTruthy()
  expect(screen.queryByRole('heading', { name: 'About' })).toBeNull()
  expectPlainTmdbCredit()
  expectNoOutboundLinks(container)
  expect(calls.some((path) => path.startsWith('/licenses/'))).toBe(false)
})

it('sends a signed-in kid away from About', async () => {
  stubApp(pairedKid)
  const { container } = renderAt('/about')
  expect(
    await screen.findByRole('button', { name: 'Leave kid mode' }),
  ).toBeTruthy()
  expectNoOutboundLinks(container)
})

it('sends a browser that is not signed in away from About', async () => {
  const calls = stubApp({
    setupRequired: false,
    principalType: null,
    kidDevice: null,
  })
  renderAt('/about')
  expect(await screen.findByRole('button', { name: 'Sign in' })).toBeTruthy()
  expect(calls.some((path) => path.startsWith('/licenses/'))).toBe(false)
})
