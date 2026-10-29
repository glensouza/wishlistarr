import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { act, cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, expect, it, vi } from 'vitest'
import App from './App'
import ParentHome from './ParentHome'
import { axe } from './test/axe'

type Call = { method: string; path: string; body: unknown }

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

const reason = {
  code: 'aboveCeiling',
  rating: 'PG-13',
  level: 'teen',
  maxLevel: 'parentalGuidance',
  genreName: null,
  keywordName: null,
  language: null,
}

const ada = {
  id: 1,
  kidProfileId: 10,
  kidName: 'Ada',
  kind: 'movie',
  tmdbId: 603,
  title: 'The Matrix',
  requestCount: 2,
  createdAt: '2026-01-02T00:00:00.000Z',
  overview: 'A hacker learns the truth.',
  rating: 'PG-13',
  descriptors: ['Violence'],
  providers: [{ name: 'Stream' }],
  trailerKey: 'M7lc1UVf-VE',
  reason,
  gaps: [],
}

const bea = {
  ...ada,
  id: 2,
  kidProfileId: 11,
  kidName: 'Bea',
  title: 'Spirited Away',
  requestCount: 1,
  overview: 'A girl walks into a spirit world.',
  rating: null,
  descriptors: [],
  providers: [],
  trailerKey: null,
  reason: {
    ...reason,
    code: 'unrated',
    rating: null,
    level: null,
    maxLevel: null,
  },
}

const profiles = [
  { id: 10, name: 'Ada' },
  { id: 11, name: 'Bea' },
]

const adaEvent = {
  id: 5,
  requestId: 1,
  title: 'The Matrix',
  fromState: 'pending',
  toState: 'denied',
  actorName: 'Parent',
  reason: null,
  occurredAt: '2026-01-02T00:00:00.000Z',
}

const beaEvent = { ...adaEvent, id: 6, requestId: 2, title: 'Spirited Away' }

function page(
  items: unknown[],
  total = items.length,
  extra: { tmdbConfigured?: boolean } = {},
) {
  return { items, total, limit: 50, offset: 0, ...extra }
}

function queuePath(path: string) {
  if (!path.startsWith('/api/v1/requests/queue')) return null
  return {
    kid: new URL(path, 'http://localhost').searchParams.get('kidProfileId'),
  }
}

function install(
  handler: (path: string, method: string) => unknown,
  approve?: (body: unknown) => Promise<Response> | Response,
) {
  const calls: Call[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (path: string, init?: RequestInit) => {
      const method = init?.method ?? 'GET'
      if (path === '/api/v1/auth/antiforgery') {
        return Response.json({ token: 'tok', headerName: 'X-CSRF-TOKEN' })
      }
      const body = init?.body ? JSON.parse(init.body as string) : undefined
      calls.push({ method, path, body })
      if (path.endsWith('/approve') || path.endsWith('/deny')) {
        if (approve) return approve(body)
        const ids =
          body &&
          typeof body === 'object' &&
          Array.isArray((body as { ids?: unknown }).ids)
            ? (body as { ids: number[] }).ids
            : []
        return Response.json({
          results: ids.map((id) => ({
            id,
            status: 'succeeded',
            state: path.endsWith('/deny') ? 'denied' : 'approved',
          })),
        })
      }
      const payload = await handler(path, method)
      if (payload instanceof Response) return payload
      if (payload === null) {
        return Response.json({ title: 'Missing' }, { status: 404 })
      }
      return Response.json(payload)
    }),
  )
  return calls
}

function homeData(items: unknown[], total?: number) {
  return install((path) => {
    const queue = queuePath(path)
    if (queue) {
      if (queue.kid === null) return page(items, total)
      return page(
        items.filter(
          (item) =>
            String((item as { kidProfileId?: unknown }).kidProfileId) ===
            queue.kid,
        ),
      )
    }
    if (path === '/api/v1/kid-profiles') return profiles
    if (path.includes('/events')) {
      return path.includes('/10/') ? [adaEvent] : [beaEvent]
    }
    return []
  })
}

it('shows the synopsis, rating, providers, trailer and why approval is needed', async () => {
  homeData([ada, bea])
  const { container } = render(<ParentHome />)
  expect(
    await screen.findByRole('button', { name: 'Approve The Matrix for Ada' }),
  ).toBeTruthy()

  expect(screen.getByText('A hacker learns the truth.')).toBeTruthy()
  expect(screen.getByText('Rated PG-13')).toBeTruthy()
  expect(screen.getByText('Violence')).toBeTruthy()
  expect(screen.getByText('Asked 2 times.')).toBeTruthy()
  const stream = screen.getByText('Stream')
  const credit = screen.getByText('Watch provider data from JustWatch')
  expect(stream.closest('section')).toBe(credit.closest('section'))
  expect(screen.queryByRole('link', { name: 'JustWatch' })).toBeNull()
  expect(
    screen.getByText(
      'It is rated PG-13, which is above the auto-approve limit for this profile.',
    ),
  ).toBeTruthy()
  expect(
    screen.getByText(
      'This title has no usable age rating, so a parent has to decide.',
    ),
  ).toBeTruthy()
  expect(screen.getByRole('button', { name: 'Play trailer' })).toBeTruthy()
  expect(container.querySelector('iframe')).toBeNull()
  expect(container.querySelector('script')).toBeNull()
  expect(container.textContent).not.toContain('youtube')
  expect(await axe(container)).toHaveNoViolations()
})

it('does not credit JustWatch or offer a trailer when none is listed', async () => {
  homeData([bea])
  render(<ParentHome />)
  expect(
    await screen.findByText('Not listed on a streaming service.'),
  ).toBeTruthy()
  expect(screen.queryByText(/JustWatch/)).toBeNull()
  expect(screen.queryByRole('button', { name: 'Play trailer' })).toBeNull()
  expect(document.querySelector('script')).toBeNull()
})

it('approves once, ignores the note, and moves focus to the next request', async () => {
  const user = userEvent.setup()
  const calls = homeData([ada, bea], 2)
  render(<ParentHome />)
  const note = await screen.findByLabelText('Note for Ada')
  await user.type(note, 'Keep this')
  await user.click(
    screen.getByRole('button', { name: 'Approve The Matrix for Ada' }),
  )

  const posted = calls.filter((call) => call.path.endsWith('/approve'))
  expect(posted).toHaveLength(1)
  expect(posted[0].body).toEqual({ ids: [1], reason: null })
  expect(calls.some((call) => call.path.endsWith('/deny'))).toBe(false)
  expect((await screen.findByRole('status')).textContent).toContain(
    'The Matrix was approved.',
  )
  expect(document.activeElement).toBe(
    screen.getByRole('button', { name: 'Approve Spirited Away for Bea' }),
  )
  expect(
    screen.queryByRole('button', { name: 'Approve The Matrix for Ada' }),
  ).toBeNull()
})

it('focuses the empty queue after the last request is approved', async () => {
  const user = userEvent.setup()
  homeData([bea])
  render(<ParentHome />)
  await user.click(
    await screen.findByRole('button', {
      name: 'Approve Spirited Away for Bea',
    }),
  )
  expect(
    await screen.findByText('Nothing is waiting for approval.'),
  ).toBeTruthy()
  expect(document.activeElement).toBe(document.getElementById('queue-empty'))
})

it('denies once with the note and sends nothing when the note is blank', async () => {
  const user = userEvent.setup()
  const calls = homeData([ada])
  render(<ParentHome />)
  await user.type(
    await screen.findByLabelText('Note for Ada'),
    '  Not tonight  ',
  )
  await user.click(
    screen.getByRole('button', { name: 'Deny The Matrix for Ada' }),
  )
  const denied = calls.filter((call) => call.path.endsWith('/deny'))
  expect(denied).toHaveLength(1)
  expect(denied[0].body).toEqual({ ids: [1], reason: 'Not tonight' })
  expect(screen.queryByRole('button', { name: 'Confirm' })).toBeNull()

  cleanup()
  const again = homeData([ada])
  render(<ParentHome />)
  await user.click(
    await screen.findByRole('button', { name: 'Deny The Matrix for Ada' }),
  )
  const blank = again.filter((call) => call.path.endsWith('/deny'))
  expect(blank).toHaveLength(1)
  expect(blank[0].body).toEqual({ ids: [1], reason: null })
})

it('refuses a note over 500 characters and still lets the parent approve', async () => {
  const user = userEvent.setup()
  const calls = homeData([ada])
  render(<ParentHome />)
  const note = (await screen.findByLabelText(
    'Note for Ada',
  )) as HTMLTextAreaElement
  note.focus()
  await user.paste('n'.repeat(501))
  await user.click(
    screen.getByRole('button', { name: 'Deny The Matrix for Ada' }),
  )
  expect(calls.some((call) => call.method === 'POST')).toBe(false)
  expect(screen.getByRole('alert').textContent).toContain(
    'That note is longer than 500 characters. Shorten it, then deny.',
  )
  await user.click(
    screen.getByRole('button', { name: 'Approve The Matrix for Ada' }),
  )
  const posted = calls.filter((call) => call.path.endsWith('/approve'))
  expect(posted).toHaveLength(1)
  expect(posted[0].body).toEqual({ ids: [1], reason: null })
})

it('sends one approval when the button is activated twice while the first is in flight', async () => {
  let release: (response: Response) => void = () => {}
  const gate = new Promise<Response>((resolve) => {
    release = resolve
  })
  const calls = install(
    (path) => {
      if (path === '/api/v1/requests/queue') return page([ada])
      if (path === '/api/v1/kid-profiles') return profiles
      if (path.includes('/events')) return []
      return []
    },
    () => gate,
  )
  render(<ParentHome />)
  const button = await screen.findByRole('button', {
    name: 'Approve The Matrix for Ada',
  })
  await act(async () => {
    button.dispatchEvent(new MouseEvent('click', { bubbles: true }))
    button.dispatchEvent(new MouseEvent('click', { bubbles: true }))
  })
  expect(calls.filter((call) => call.path.endsWith('/approve'))).toHaveLength(1)
  await act(async () => {
    release(
      Response.json({
        results: [{ id: 1, status: 'succeeded', state: 'approved' }],
      }),
    )
  })
})

it('filters the queue and recent activity by kid', async () => {
  const user = userEvent.setup()
  const calls = homeData([ada, bea])
  render(<ParentHome />)
  expect(
    (await screen.findByRole('list', { name: 'Recent activity for Ada' }))
      .textContent,
  ).toContain('Pending')
  expect(
    screen.getByRole('list', { name: 'Recent activity for Ada' }).textContent,
  ).toContain('Denied')
  expect(
    calls.some(
      (call) => call.path === '/api/v1/kid-profiles/10/events?limit=5',
    ),
  ).toBe(true)
  expect(
    screen.getByRole('list', { name: 'Recent activity for Bea' }),
  ).toBeTruthy()

  await user.selectOptions(screen.getByLabelText('Show requests for'), 'Ada')
  expect(
    screen.getByRole('button', { name: 'Approve The Matrix for Ada' }),
  ).toBeTruthy()
  expect(
    screen.queryByRole('button', { name: 'Approve Spirited Away for Bea' }),
  ).toBeNull()
  expect(
    screen.queryByRole('list', { name: 'Recent activity for Bea' }),
  ).toBeNull()
  expect(
    screen.getByRole('list', { name: 'Recent activity for Ada' }),
  ).toBeTruthy()
})

it('keeps the queue usable when profiles cannot be loaded', async () => {
  install((path) => {
    if (queuePath(path)) return page([ada])
    if (path === '/api/v1/kid-profiles') return null
    if (path.includes('/events')) return [adaEvent]
    return []
  })
  render(<ParentHome />)
  expect(
    await screen.findByText('Kid profiles could not be loaded.'),
  ).toBeTruthy()
  await userSelectAda()
  expect(
    screen.getByRole('button', { name: 'Approve The Matrix for Ada' }),
  ).toBeTruthy()
})

async function userSelectAda() {
  const user = userEvent.setup()
  await user.selectOptions(
    await screen.findByLabelText('Show requests for'),
    'Ada',
  )
}

it('links quick actions to the sections on the parent page', async () => {
  install((path) => {
    if (path === '/api/v1/setup') return { completed: true, showGuide: false }
    if (path === '/api/v1/auth/status') {
      return { setupRequired: false, principalType: 'Parent', kidDevice: null }
    }
    if (
      path === '/api/v1/requests/queue' ||
      path.startsWith('/api/v1/requests')
    ) {
      return page([])
    }
    if (path === '/api/v1/backups') {
      return { retention: 7, restorePending: false, backups: [] }
    }
    if (path === '/api/v1/updates')
      return { checkEnabled: true, available: null }
    return []
  })
  render(
    <MemoryRouter initialEntries={['/parent']}>
      <App />
    </MemoryRouter>,
  )
  const nav = await screen.findByRole('navigation', { name: 'Quick actions' })
  const links = [...nav.querySelectorAll('a')].map((link) => ({
    text: link.textContent,
    href: link.getAttribute('href'),
  }))
  expect(links).toEqual([
    { text: 'All requests', href: '#requests-heading' },
    { text: 'Kid profiles', href: '#kid-profiles-heading' },
    { text: 'Kid devices', href: '#kid-devices-heading' },
    { text: 'Full history', href: '#request-history-heading' },
    { text: 'Backups', href: '#backups-heading' },
    { text: 'Logs', href: '#logs-heading' },
    { text: 'Trailer setting', href: '#trailers-heading' },
  ])
  await screen.findByRole('heading', { name: 'YouTube trailers' })
  for (const link of links) {
    expect(document.getElementById(link.href!.slice(1))).toBeTruthy()
  }
})

it('lays the queue out in one column with large tap targets', async () => {
  const css = readFileSync(join(__dirname, 'styles.css'), 'utf8')
  expect(css).toMatch(/\.parent-home\s*\{[^}]*overflow-x:\s*clip/)
  expect(css).toMatch(
    /\.quick-actions,\s*\.approval-queue,\s*\.approval-actions\s*\{[^}]*grid-template-columns:\s*minmax\(0,\s*1fr\)/,
  )
  expect(css).toMatch(
    /\.quick-actions a,\s*\.approval-actions button\s*\{[^}]*min-height:\s*var\(--tap-size\)/,
  )
  expect(css).toMatch(/\.approval-card\s*\{[^}]*min-width:\s*0/)
  expect(css).toMatch(/\.approval-card\s*\{[^}]*overflow-wrap:\s*anywhere/)

  homeData([ada])
  render(<ParentHome />)
  const nav = await screen.findByRole('navigation', { name: 'Quick actions' })
  expect(nav.className).toContain('quick-actions')
  const card = screen
    .getByRole('button', { name: 'Approve The Matrix for Ada' })
    .closest('article')
  expect(card?.className).toContain('approval-card')
  expect(
    screen
      .getByRole('button', { name: 'Deny The Matrix for Ada' })
      .closest('.approval-actions'),
  ).toBeTruthy()
})

it('names a queue failure, a bad payload, and a dropped connection differently', async () => {
  const user = userEvent.setup()
  let mode: 'down' | 'shape' | 'drop' | 'ready' = 'down'
  let queueGets = 0
  install((path) => {
    if (path === '/api/v1/requests/queue') {
      queueGets += 1
      if (mode === 'down') {
        return Response.json({ title: 'The catalog is down.' }, { status: 500 })
      }
      if (mode === 'shape') return { items: [{}] }
      if (mode === 'drop') throw new TypeError('Failed to fetch')
      return page([])
    }
    if (path === '/api/v1/kid-profiles') return []
    return []
  })
  render(<ParentHome />)
  expect((await screen.findByRole('alert')).textContent).toContain(
    'The catalog is down.',
  )

  mode = 'shape'
  await user.click(screen.getByRole('button', { name: 'Try again' }))
  expect((await screen.findByRole('alert')).textContent).toContain(
    'The approval queue came back in a shape this page does not understand.',
  )
  expect(screen.queryByText('Failed to fetch')).toBeNull()

  mode = 'drop'
  await user.click(screen.getByRole('button', { name: 'Try again' }))
  expect((await screen.findByRole('alert')).textContent).toContain(
    'The approval queue could not be loaded.',
  )
  expect(screen.queryByText('Failed to fetch')).toBeNull()

  mode = 'ready'
  await user.click(screen.getByRole('button', { name: 'Try again' }))
  expect(
    await screen.findByText('Nothing is waiting for approval.'),
  ).toBeTruthy()
  expect(queueGets).toBe(4)
})

it('shows which part of a title could not be loaded', async () => {
  homeData([
    {
      ...ada,
      overview: null,
      rating: null,
      descriptors: [],
      providers: [],
      trailerKey: null,
      reason: { ...reason, code: 'nope' },
      gaps: ['synopsis', 'rating', 'providers', 'trailer'],
    },
  ])
  render(<ParentHome />)
  expect(
    await screen.findByText('The synopsis could not be loaded.'),
  ).toBeTruthy()
  expect(screen.getByText('The age rating could not be loaded.')).toBeTruthy()
  expect(
    screen.getByText('Streaming availability could not be loaded.'),
  ).toBeTruthy()
  expect(screen.getByText('The trailer could not be loaded.')).toBeTruthy()
  expect(screen.queryByText(/JustWatch/)).toBeNull()
  expect(screen.queryByRole('button', { name: 'Play trailer' })).toBeNull()
  expect(
    screen.getByText(
      'Why this title needs approval could not be checked. You can still approve or deny it.',
    ),
  ).toBeTruthy()
})

it('keeps the request and explains a decision the server did not apply', async () => {
  const user = userEvent.setup()
  install(
    (path) => {
      if (path === '/api/v1/requests/queue') return page([ada])
      if (path === '/api/v1/kid-profiles') return profiles
      if (path.includes('/events')) return []
      return []
    },
    () =>
      Response.json({
        results: [{ id: 1, status: 'conflict', state: 'pending' }],
      }),
  )
  render(<ParentHome />)
  await user.click(
    await screen.findByRole('button', { name: 'Deny The Matrix for Ada' }),
  )
  const alert = await screen.findByRole('alert')
  expect(alert.textContent).toContain('Someone else decided it differently')
  expect(document.activeElement).toBe(alert)
  expect(
    screen.getByRole('button', { name: 'Deny The Matrix for Ada' }),
  ).toBeTruthy()
})

it('says how many requests are past this page', async () => {
  homeData([ada], 3)
  render(<ParentHome />)
  const more = await screen.findByText(/2 more are waiting/)
  expect(more.querySelector('a')?.getAttribute('href')).toBe(
    '#requests-heading',
  )
  expect(more.querySelector('a')?.textContent).toBe('All requests')
})

it('asks the server for a kid whose requests are not on the first page', async () => {
  const user = userEvent.setup()
  const calls = install((path) => {
    const queue = queuePath(path)
    if (queue?.kid === '11') return page([bea], 10)
    if (queue) return page([ada], 60)
    if (path === '/api/v1/kid-profiles') return profiles
    if (path.includes('/events')) return []
    return []
  })
  render(<ParentHome />)
  expect(await screen.findByText(/59 more are waiting/)).toBeTruthy()
  expect(
    screen.queryByRole('button', { name: 'Approve Spirited Away for Bea' }),
  ).toBeNull()

  await user.selectOptions(screen.getByLabelText('Show requests for'), 'Bea')
  expect(
    await screen.findByRole('button', {
      name: 'Approve Spirited Away for Bea',
    }),
  ).toBeTruthy()
  expect(
    screen.queryByRole('button', { name: 'Approve The Matrix for Ada' }),
  ).toBeNull()
  expect(screen.queryByText('Nothing is waiting for Bea.')).toBeNull()
  expect(screen.getByText(/9 more are waiting/)).toBeTruthy()

  await user.selectOptions(
    screen.getByLabelText('Show requests for'),
    'All kids',
  )
  expect(
    await screen.findByRole('button', { name: 'Approve The Matrix for Ada' }),
  ).toBeTruthy()
  expect(screen.getByText(/59 more are waiting/)).toBeTruthy()
  expect(
    calls
      .filter((call) => call.path.startsWith('/api/v1/requests/queue'))
      .map((call) => call.path),
  ).toEqual([
    '/api/v1/requests/queue',
    '/api/v1/requests/queue?kidProfileId=11',
    '/api/v1/requests/queue',
  ])
})

it('shows the server error when a kid filter fails and keeps the filter usable', async () => {
  const user = userEvent.setup()
  install((path) => {
    const queue = queuePath(path)
    if (queue?.kid === '11') {
      return Response.json(
        { title: 'The kid profile does not exist.' },
        { status: 404 },
      )
    }
    if (queue) return page([ada, bea])
    if (path === '/api/v1/kid-profiles') return profiles
    if (path.includes('/events')) return []
    return []
  })
  render(<ParentHome />)
  await screen.findByRole('button', { name: 'Approve The Matrix for Ada' })
  const filter = screen.getByLabelText('Show requests for')
  await user.selectOptions(filter, 'Bea')

  expect((await screen.findByRole('alert')).textContent).toContain(
    'The kid profile does not exist.',
  )
  expect(
    screen.queryByText('The approval queue could not be loaded.'),
  ).toBeNull()
  expect(screen.queryByText('Nothing is waiting for Bea.')).toBeNull()
  expect((filter as HTMLSelectElement).disabled).toBe(false)

  await user.selectOptions(filter, 'All kids')
  expect(
    await screen.findByRole('button', { name: 'Approve The Matrix for Ada' }),
  ).toBeTruthy()
  expect(screen.queryByRole('alert')).toBeNull()
})

it('ignores a queue page that arrives after another kid was chosen', async () => {
  const user = userEvent.setup()
  let releaseBea: (response: Response) => void = () => {}
  const beaGate = new Promise<Response>((resolve) => {
    releaseBea = resolve
  })
  const calls = install((path) => {
    const queue = queuePath(path)
    if (queue?.kid === '11') return beaGate
    if (queue?.kid === '10') return page([ada], 1)
    if (queue) return page([ada, bea], 2)
    if (path === '/api/v1/kid-profiles') return profiles
    if (path.includes('/events')) return []
    return []
  })
  render(<ParentHome />)
  await screen.findByRole('button', { name: 'Approve The Matrix for Ada' })

  await user.selectOptions(screen.getByLabelText('Show requests for'), 'Bea')
  expect(screen.getByText('Loading the approval queue')).toBeTruthy()
  expect(
    (screen.getByLabelText('Show requests for') as HTMLSelectElement).disabled,
  ).toBe(false)
  expect(screen.queryByText('Nothing is waiting for Bea.')).toBeNull()
  expect(
    calls.some(
      (call) => call.path === '/api/v1/requests/queue?kidProfileId=11',
    ),
  ).toBe(true)

  await user.selectOptions(screen.getByLabelText('Show requests for'), 'Ada')
  expect(
    await screen.findByRole('button', { name: 'Approve The Matrix for Ada' }),
  ).toBeTruthy()
  expect(
    screen.queryByRole('button', { name: 'Approve Spirited Away for Bea' }),
  ).toBeNull()

  await act(async () => {
    releaseBea(Response.json(page([bea], 1)))
  })
  expect(
    screen.getByRole('button', { name: 'Approve The Matrix for Ada' }),
  ).toBeTruthy()
  expect(
    screen.queryByRole('button', { name: 'Approve Spirited Away for Bea' }),
  ).toBeNull()
})

const cy = {
  ...ada,
  id: 3,
  kidProfileId: 12,
  kidName: 'Cy',
  title: 'Up',
  requestCount: 1,
  providers: [],
  trailerKey: null,
}

it('moves focus to the previous request when the last one is decided', async () => {
  const user = userEvent.setup()
  homeData([ada, bea])
  render(<ParentHome />)
  await user.click(
    await screen.findByRole('button', {
      name: 'Approve Spirited Away for Bea',
    }),
  )
  expect(document.activeElement).toBe(
    screen.getByRole('button', { name: 'Approve The Matrix for Ada' }),
  )
  expect(
    screen.queryByRole('button', { name: 'Approve Spirited Away for Bea' }),
  ).toBeNull()
})

it('keeps focus off a request that is still being decided', async () => {
  const releases = new Map<number, (response: Response) => void>()
  install(
    (path) => {
      if (queuePath(path)) return page([ada, bea])
      if (path === '/api/v1/kid-profiles') return profiles
      if (path.includes('/events')) return []
      return []
    },
    (body) =>
      new Promise((resolve) => {
        const ids =
          body &&
          typeof body === 'object' &&
          Array.isArray((body as { ids?: unknown }).ids)
            ? (body as { ids: number[] }).ids
            : []
        releases.set(ids[0] ?? 0, resolve)
      }),
  )
  render(<ParentHome />)
  const approveAda = await screen.findByRole('button', {
    name: 'Approve The Matrix for Ada',
  })
  const approveBea = screen.getByRole('button', {
    name: 'Approve Spirited Away for Bea',
  })
  await act(async () => {
    approveBea.dispatchEvent(new MouseEvent('click', { bubbles: true }))
    approveAda.dispatchEvent(new MouseEvent('click', { bubbles: true }))
  })

  await act(async () => {
    releases.get(2)?.(
      Response.json({
        results: [{ id: 2, status: 'succeeded', state: 'approved' }],
      }),
    )
  })
  expect(document.activeElement).toBe(document.getElementById('queue-kid'))
  expect(
    screen.queryByRole('button', { name: 'Approve Spirited Away for Bea' }),
  ).toBeNull()
  expect(
    (
      screen.getByRole('button', {
        name: 'Approve The Matrix for Ada',
      }) as HTMLButtonElement
    ).disabled,
  ).toBe(true)

  await act(async () => {
    releases.get(1)?.(
      Response.json({
        results: [{ id: 1, status: 'succeeded', state: 'approved' }],
      }),
    )
  })
  expect(document.activeElement).toBe(document.getElementById('queue-empty'))
  expect(
    screen.queryByRole('button', { name: 'Approve The Matrix for Ada' }),
  ).toBeNull()
  expect(screen.getByText('Nothing is waiting for approval.')).toBeTruthy()
})

it('moves focus past a request that is still being decided', async () => {
  const releases = new Map<number, (response: Response) => void>()
  install(
    (path) => {
      if (queuePath(path)) return page([ada, bea, cy])
      if (path === '/api/v1/kid-profiles') return profiles
      if (path.includes('/events')) return []
      return []
    },
    (body) =>
      new Promise((resolve) => {
        const ids =
          body &&
          typeof body === 'object' &&
          Array.isArray((body as { ids?: unknown }).ids)
            ? (body as { ids: number[] }).ids
            : []
        releases.set(ids[0] ?? 0, resolve)
      }),
  )
  render(<ParentHome />)
  const approveAda = await screen.findByRole('button', {
    name: 'Approve The Matrix for Ada',
  })
  const approveBea = screen.getByRole('button', {
    name: 'Approve Spirited Away for Bea',
  })
  await act(async () => {
    approveAda.dispatchEvent(new MouseEvent('click', { bubbles: true }))
    approveBea.dispatchEvent(new MouseEvent('click', { bubbles: true }))
  })
  expect((approveBea as HTMLButtonElement).disabled).toBe(true)

  await act(async () => {
    releases.get(1)?.(
      Response.json({
        results: [{ id: 1, status: 'succeeded', state: 'approved' }],
      }),
    )
  })
  expect(document.activeElement).toBe(
    screen.getByRole('button', { name: 'Approve Up for Cy' }),
  )
  expect(
    (
      screen.getByRole('button', {
        name: 'Approve Spirited Away for Bea',
      }) as HTMLButtonElement
    ).disabled,
  ).toBe(true)

  await act(async () => {
    releases.get(2)?.(
      Response.json({
        results: [{ id: 2, status: 'succeeded', state: 'approved' }],
      }),
    )
  })
  expect(document.activeElement).toBe(
    screen.getByRole('button', { name: 'Approve Up for Cy' }),
  )
  expect(
    screen.queryByRole('button', { name: 'Approve The Matrix for Ada' }),
  ).toBeNull()
})

it('shows a keyword gap and still loads the card when another gap is unknown', async () => {
  homeData([
    {
      ...ada,
      gaps: ['keywords', 'posters'],
    },
  ])
  render(<ParentHome />)
  expect(
    await screen.findByText('The keywords for this title could not be loaded.'),
  ).toBeTruthy()
  expect(screen.getByText('A hacker learns the truth.')).toBeTruthy()
  expect(
    screen.getByRole('button', { name: 'Approve The Matrix for Ada' }),
  ).toBeTruthy()
  expect(screen.queryByRole('alert')).toBeNull()
  expect(screen.queryByText('posters')).toBeNull()
})

const bare = {
  overview: null,
  rating: null,
  descriptors: [],
  providers: [],
  trailerKey: null,
  gaps: ['synopsis', 'rating', 'providers', 'trailer', 'keywords'],
}

it('says once that TMDB is not configured and still lets the parent decide', async () => {
  const user = userEvent.setup()
  const calls = install((path) => {
    if (queuePath(path)) {
      return page(
        [
          { ...ada, ...bare },
          { ...bea, ...bare },
        ],
        2,
        { tmdbConfigured: false },
      )
    }
    if (path === '/api/v1/kid-profiles') return profiles
    if (path.includes('/events')) return []
    return []
  })
  const { container } = render(<ParentHome />)
  expect(
    await screen.findByRole('button', { name: 'Approve The Matrix for Ada' }),
  ).toBeTruthy()
  expect(screen.getAllByText(/TMDB is not configured/)).toHaveLength(1)
  expect(screen.queryByText('The synopsis could not be loaded.')).toBeNull()
  expect(screen.queryByText('The age rating could not be loaded.')).toBeNull()
  expect(
    screen.queryByText('Streaming availability could not be loaded.'),
  ).toBeNull()
  expect(screen.queryByText('The trailer could not be loaded.')).toBeNull()
  expect(
    screen.queryByText('The keywords for this title could not be loaded.'),
  ).toBeNull()
  expect(screen.queryByText('No synopsis is available.')).toBeNull()
  expect(screen.queryByText('No age rating is listed.')).toBeNull()
  expect(screen.queryByText('No content descriptors are listed.')).toBeNull()
  expect(screen.queryByText('Not listed on a streaming service.')).toBeNull()
  expect(screen.getByText('The Matrix')).toBeTruthy()
  expect(screen.getByText('Spirited Away')).toBeTruthy()
  expect(await axe(container)).toHaveNoViolations()

  await user.click(
    screen.getByRole('button', { name: 'Approve The Matrix for Ada' }),
  )
  expect(
    screen.queryByRole('button', { name: 'Approve The Matrix for Ada' }),
  ).toBeNull()
  expect(
    screen.getByRole('button', { name: 'Approve Spirited Away for Bea' }),
  ).toBeTruthy()
  expect(screen.getAllByText(/TMDB is not configured/)).toHaveLength(1)
  expect(calls.filter((call) => call.path.endsWith('/approve'))).toHaveLength(1)
})

it('keeps the selected kid when profiles fail and names that empty queue', async () => {
  const user = userEvent.setup()
  let releaseAda: (response: Response) => void = () => {}
  const adaGate = new Promise<Response>((resolve) => {
    releaseAda = resolve
  })
  install((path) => {
    const queue = queuePath(path)
    if (queue?.kid === '10') return adaGate
    if (queue) return page([ada, bea])
    if (path === '/api/v1/kid-profiles') return null
    if (path.includes('/events')) return []
    return []
  })
  render(<ParentHome />)
  await screen.findByRole('button', { name: 'Approve The Matrix for Ada' })
  await user.selectOptions(screen.getByLabelText('Show requests for'), 'Ada')

  const filter = screen.getByLabelText('Show requests for') as HTMLSelectElement
  expect(filter.value).toBe('10')
  expect(filter.selectedOptions[0]?.textContent).toBe('Ada')
  expect(screen.getByText('Loading the approval queue')).toBeTruthy()
  expect(screen.queryByText('Nothing is waiting for approval.')).toBeNull()

  await act(async () => {
    releaseAda(Response.json(page([])))
  })
  expect(await screen.findByText('Nothing is waiting for Ada.')).toBeTruthy()
  expect(screen.queryByText('Nothing is waiting for approval.')).toBeNull()
  const selected = screen.getByLabelText(
    'Show requests for',
  ) as HTMLSelectElement
  expect(selected.value).toBe('10')
  expect(selected.selectedOptions[0]?.textContent).toBe('Ada')
  expect(
    screen.queryByRole('button', { name: 'Approve The Matrix for Ada' }),
  ).toBeNull()
})

it('drops a request decided while a filtered reload is still in flight', async () => {
  const user = userEvent.setup()
  let releaseReload: (response: Response) => void = () => {}
  const reload = new Promise<Response>((resolve) => {
    releaseReload = resolve
  })
  let releaseDecision: (response: Response) => void = () => {}
  const decision = new Promise<Response>((resolve) => {
    releaseDecision = resolve
  })
  install(
    (path) => {
      const queue = queuePath(path)
      if (queue?.kid === '10') return reload
      if (queue) return page([ada, bea])
      if (path === '/api/v1/kid-profiles') return profiles
      if (path.includes('/events')) return []
      return []
    },
    () => decision,
  )
  render(<ParentHome />)
  const approveAda = await screen.findByRole('button', {
    name: 'Approve The Matrix for Ada',
  })
  await act(async () => {
    approveAda.dispatchEvent(new MouseEvent('click', { bubbles: true }))
  })
  await user.selectOptions(screen.getByLabelText('Show requests for'), 'Ada')
  expect(screen.getByText('Loading the approval queue')).toBeTruthy()

  await act(async () => {
    releaseDecision(
      Response.json({
        results: [{ id: 1, status: 'succeeded', state: 'approved' }],
      }),
    )
  })
  expect(
    screen.queryByRole('button', { name: 'Approve The Matrix for Ada' }),
  ).toBeNull()

  await act(async () => {
    releaseReload(Response.json(page([ada], 1)))
  })
  expect(
    screen.queryByRole('button', { name: 'Approve The Matrix for Ada' }),
  ).toBeNull()
  expect(screen.getByText('Nothing is waiting for Ada.')).toBeTruthy()
  expect(screen.queryByText('Nothing is waiting for approval.')).toBeNull()
})

it('drops a request when the stale page arrives before the decision', async () => {
  const user = userEvent.setup()
  let releaseReload: (response: Response) => void = () => {}
  const reload = new Promise<Response>((resolve) => {
    releaseReload = resolve
  })
  let releaseDecision: (response: Response) => void = () => {}
  const decision = new Promise<Response>((resolve) => {
    releaseDecision = resolve
  })
  install(
    (path) => {
      const queue = queuePath(path)
      if (queue?.kid === '10') return reload
      if (queue) return page([ada, bea])
      if (path === '/api/v1/kid-profiles') return profiles
      if (path.includes('/events')) return []
      return []
    },
    () => decision,
  )
  render(<ParentHome />)
  const approveAda = await screen.findByRole('button', {
    name: 'Approve The Matrix for Ada',
  })
  await act(async () => {
    approveAda.dispatchEvent(new MouseEvent('click', { bubbles: true }))
  })
  await user.selectOptions(screen.getByLabelText('Show requests for'), 'Ada')
  await act(async () => {
    releaseReload(Response.json(page([ada], 1)))
  })
  expect(
    await screen.findByRole('button', { name: 'Approve The Matrix for Ada' }),
  ).toBeTruthy()

  await act(async () => {
    releaseDecision(
      Response.json({
        results: [{ id: 1, status: 'succeeded', state: 'approved' }],
      }),
    )
  })
  expect(
    screen.queryByRole('button', { name: 'Approve The Matrix for Ada' }),
  ).toBeNull()
  expect(screen.getByText('Nothing is waiting for Ada.')).toBeTruthy()
  await waitFor(() =>
    expect(document.activeElement).toBe(document.getElementById('queue-empty')),
  )
})

it('loads the next page when the visible requests are decided and more are waiting', async () => {
  const user = userEvent.setup()
  let releaseNext: (response: Response) => void = () => {}
  const nextGate = new Promise<Response>((resolve) => {
    releaseNext = resolve
  })
  let queueCalls = 0
  install((path) => {
    const queue = queuePath(path)
    if (queue) {
      queueCalls += 1
      if (queueCalls === 1) return page([ada], 2)
      return nextGate
    }
    if (path === '/api/v1/kid-profiles') return profiles
    if (path.includes('/events')) return []
    return []
  })
  render(<ParentHome />)
  await user.click(
    await screen.findByRole('button', { name: 'Approve The Matrix for Ada' }),
  )
  expect(await screen.findByText('Loading the approval queue')).toBeTruthy()
  expect(screen.queryByText('Nothing is waiting for approval.')).toBeNull()
  expect(screen.queryByText(/more are waiting/)).toBeNull()
  expect(
    screen.queryByRole('button', { name: 'Approve The Matrix for Ada' }),
  ).toBeNull()
  await waitFor(() =>
    expect(document.activeElement).toBe(
      document.getElementById('queue-loading'),
    ),
  )
  expect(queueCalls).toBe(2)

  await act(async () => {
    releaseNext(Response.json(page([bea], 1)))
  })
  const next = await screen.findByRole('button', {
    name: 'Approve Spirited Away for Bea',
  })
  await waitFor(() => expect(document.activeElement).toBe(next))
  expect(screen.queryByText('Nothing is waiting for approval.')).toBeNull()
  expect(screen.queryByText('Nothing is waiting for Bea.')).toBeNull()
  expect(screen.queryByText(/more are waiting/)).toBeNull()
})

it('shows the load error when the next page cannot be loaded', async () => {
  const user = userEvent.setup()
  let queueCalls = 0
  install((path) => {
    const queue = queuePath(path)
    if (queue) {
      queueCalls += 1
      if (queueCalls === 1) return page([ada], 2)
      return Response.json({ title: 'The catalog is down.' }, { status: 500 })
    }
    if (path === '/api/v1/kid-profiles') return profiles
    if (path.includes('/events')) return []
    return []
  })
  render(<ParentHome />)
  await user.click(
    await screen.findByRole('button', { name: 'Approve The Matrix for Ada' }),
  )
  const alert = await screen.findByRole('alert')
  expect(alert.textContent).toContain('The catalog is down.')
  await waitFor(() => expect(document.activeElement).toBe(alert))
  expect(screen.queryByText('Nothing is waiting for approval.')).toBeNull()
  expect(
    screen.queryByRole('button', { name: 'Approve The Matrix for Ada' }),
  ).toBeNull()
})

it('loads more from the control when a page is empty but requests remain', async () => {
  const user = userEvent.setup()
  let queueCalls = 0
  install((path) => {
    const queue = queuePath(path)
    if (queue) {
      queueCalls += 1
      if (queueCalls === 1) return page([], 2)
      return page([ada], 2)
    }
    if (path === '/api/v1/kid-profiles') return profiles
    if (path.includes('/events')) return []
    return []
  })
  render(<ParentHome />)
  expect(await screen.findByText(/2 more are waiting/)).toBeTruthy()
  expect(screen.queryByText('Nothing is waiting for approval.')).toBeNull()
  expect(screen.queryByText(/Decide these/)).toBeNull()
  await user.click(
    screen.getByRole('button', { name: 'Load the next requests' }),
  )
  const approve = await screen.findByRole('button', {
    name: 'Approve The Matrix for Ada',
  })
  await waitFor(() => expect(document.activeElement).toBe(approve))
  expect(screen.queryByText('Nothing is waiting for approval.')).toBeNull()
  expect(screen.getByText(/1 more are waiting/)).toBeTruthy()
})
