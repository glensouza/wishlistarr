import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from '@testing-library/react'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import ContentControls from './ContentControls'
import { exportRules, importRules, profileRules } from './contentRules'
import type { KidProfile } from './kidProfiles'

const ada: KidProfile = {
  id: 1,
  name: 'Ada',
  avatar: 'fox',
  hasPin: true,
  movieAgeLimit: 7,
  tvAgeLimit: 10,
  weeklyAllowance: 3,
  autoApprove: false,
  autoApproveMovieRating: null,
  autoApproveTvRating: null,
  blockedGenreIds: [],
  blockedKeywordIds: [],
  allowedOriginalLanguages: [],
  blockedDescriptors: [],
  blockedTopics: [],
  plexLabel: 'Ada',
  plexUserId: '42',
  homeRows: ['library'],
  isLocked: false,
}
let writes: { id: string; body: Record<string, unknown> }[]
let failure: boolean
let gate: Promise<void> | null
beforeEach(() => {
  writes = []
  failure = false
  gate = null
  vi.stubGlobal(
    'fetch',
    vi.fn(async (path: string, init?: RequestInit) => {
      if (path === '/api/v1/auth/antiforgery')
        return Response.json({ token: 'token', headerName: 'X-CSRF-TOKEN' })
      if (path.includes('/titles?'))
        return Response.json({ total: 0, items: [] })
      if (path.endsWith('/content-rules') && init?.method !== 'PUT')
        return Response.json(JSON.parse(exportRules(ada)))
      if (init?.method === 'PUT') {
        const body = JSON.parse(init.body as string) as Record<string, unknown>
        writes.push({ id: path, body })
        if (gate) await gate
        if (failure) return Response.json({ title: 'Failed' }, { status: 500 })
        return Response.json({ ...ada, ...((body.rules as object) ?? body) })
      }
      return Response.json([ada, { ...ada, id: 2, name: 'Bea' }])
    }),
  )
})
afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})
async function open() {
  render(<ContentControls />)
  fireEvent.click(
    screen.getByRole('button', { name: 'Manage content controls' }),
  )
  await screen.findByRole('option', { name: 'Ada' })
  fireEvent.change(screen.getByLabelText('Kid profile'), {
    target: { value: '1' },
  })
}
function field(name: string) {
  return screen.getByLabelText(name, { exact: false })
}

it('saves an age change automatically, preserves unrelated settings and prevents switching while saving', async () => {
  await open()
  let release: () => void = () => {}
  gate = new Promise<void>((resolve) => {
    release = resolve
  })
  fireEvent.change(field('Movie age limit'), { target: { value: '13' } })
  expect(screen.getByRole('status').textContent).toBe('Saving…')
  expect((field('Kid profile') as HTMLSelectElement).disabled).toBe(true)
  await waitFor(() => expect(writes).toHaveLength(1))
  expect(writes[0].id).toBe('/api/v1/kid-profiles/1')
  expect(writes[0].body).toMatchObject({
    movieAgeLimit: 13,
    tvAgeLimit: 10,
    plexUserId: '42',
    homeRows: ['library'],
  })
  expect(writes[0].body).not.toHaveProperty('pin')
  release()
  await screen.findByText('Saved')
  expect((field('Kid profile') as HTMLSelectElement).disabled).toBe(false)
})

it('keeps failed edits visible and retries without claiming they were saved', async () => {
  await open()
  failure = true
  fireEvent.change(field('Weekly requests'), { target: { value: '5' } })
  expect((await screen.findByRole('alert')).textContent).toContain(
    'could not save',
  )
  expect((field('Weekly requests') as HTMLInputElement).value).toBe('5')
  expect(screen.queryByText('Saved')).toBeNull()
  failure = false
  fireEvent.click(screen.getByRole('button', { name: 'Try again' }))
  await screen.findByText('Saved')
  expect(writes).toHaveLength(2)
  expect(writes[1].body.weeklyAllowance).toBe(5)
})

it('saves genre, keyword, language and approval rules for the selected profile', async () => {
  await open()
  fireEvent.change(field('Blocked genre'), { target: { value: '28, 27' } })
  fireEvent.change(field('Blocked keyword'), { target: { value: '123' } })
  fireEvent.change(field('Allowed languages'), { target: { value: 'EN, ja' } })
  fireEvent.click(field('Auto-approve requests'))
  fireEvent.change(field('Movie rating ceiling'), { target: { value: 'teen' } })
  await screen.findByText('Saved')
  expect(writes).toHaveLength(1)
  expect(writes[0].body).toMatchObject({
    blockedGenreIds: [28, 27],
    blockedKeywordIds: [123],
    allowedOriginalLanguages: ['en', 'ja'],
    autoApprove: true,
    autoApproveMovieRating: 'teen',
  })
})

it('saves descriptor and topic blocks as text', async () => {
  await open()
  fireEvent.change(field('Blocked descriptors'), {
    target: { value: ' Violence , Language' },
  })
  fireEvent.change(field('Blocked topics'), {
    target: { value: 'a dog dies' },
  })
  await screen.findByText('Saved')
  expect(writes.at(-1)?.body).toMatchObject({
    blockedDescriptors: ['Violence', 'Language'],
    blockedTopics: ['a dog dies'],
    blockedGenreIds: [],
  })
})

it('does not save a descriptor that is too long', async () => {
  await open()
  fireEvent.change(field('Blocked descriptors'), {
    target: { value: 'a'.repeat(101) },
  })
  expect(screen.getByRole('alert').textContent).toContain('have not been saved')
  expect(writes).toHaveLength(0)
})

it('does not send invalid drafts and allows discarding them', async () => {
  await open()
  fireEvent.change(field('Blocked keyword'), {
    target: { value: '123, invalid' },
  })
  expect(screen.getByRole('alert').textContent).toContain('have not been saved')
  fireEvent.click(
    screen.getByRole('button', { name: 'Discard unsaved changes' }),
  )
  expect((field('Blocked keyword') as HTMLInputElement).value).toBe('')
  expect(writes).toHaveLength(0)
})

async function upload(text: string) {
  const file = new File([text], 'rules.json', { type: 'application/json' })
  Object.defineProperty(file, 'text', { value: async () => text })
  fireEvent.change(field('Import rules from JSON'), {
    target: { files: [file] },
  })
}

it('imports a complete rules file with one update and rejects a partly valid file without changing rules', async () => {
  await open()
  const rules = {
    ...profileRules(ada),
    movieAgeLimit: 13,
    blockedGenreIds: [28],
  }
  const titles = [
    { kind: 'movie' as const, tmdbId: 603, title: 'The Matrix', blocked: true },
    { kind: 'show' as const, tmdbId: 82728, title: 'Bluey', blocked: false },
  ]
  await upload(exportRules(rules, titles))
  await screen.findByText('Saved')
  expect(writes).toHaveLength(1)
  expect(writes[0].body.rules).toMatchObject(rules)
  expect(writes[0].body.titles).toEqual(titles)
  await upload(
    JSON.stringify({
      version: 1,
      rules: { ...rules, movieAgeLimit: 18, blockedKeywordIds: [-1] },
    }),
  )
  expect((await screen.findByRole('alert')).textContent).toContain(
    'No rules were changed',
  )
  expect((field('Movie age limit') as HTMLInputElement).value).toBe('13')
  expect(writes).toHaveLength(1)
})

it('downloads only the saved profile rules as importable JSON', async () => {
  await open()
  let blob: Blob | undefined
  Object.defineProperty(URL, 'createObjectURL', {
    configurable: true,
    value: vi.fn((value: Blob) => {
      blob = value
      return 'blob:rules'
    }),
  })
  Object.defineProperty(URL, 'revokeObjectURL', {
    configurable: true,
    value: vi.fn(),
  })
  const click = vi
    .spyOn(HTMLAnchorElement.prototype, 'click')
    .mockImplementation(() => {})
  fireEvent.click(screen.getByRole('button', { name: 'Export rules as JSON' }))
  await waitFor(() => expect(click).toHaveBeenCalledOnce())
  const text = await new Promise<string>((resolve) => {
    const reader = new FileReader()
    reader.onload = () => resolve(reader.result as string)
    reader.readAsText(blob!)
  })
  expect(importRules(text)).toEqual(profileRules(ada))
  expect(text).not.toContain('plexUserId')
  expect(text).not.toContain('hasPin')
})

it.each([
  '{',
  JSON.stringify({ version: 2, rules: profileRules(ada) }),
  JSON.stringify({
    version: 1,
    rules: { ...profileRules(ada), movieAgeLimit: 22 },
  }),
  JSON.stringify({
    version: 1,
    rules: { ...profileRules(ada), autoApprove: 'true' },
  }),
  JSON.stringify({
    version: 1,
    rules: { ...profileRules(ada), autoApproveMovieRating: 'unknown' },
  }),
  JSON.stringify({
    version: 1,
    rules: { ...profileRules(ada), allowedOriginalLanguages: ['en-US'] },
  }),
  JSON.stringify({
    version: 1,
    rules: { ...profileRules(ada), blockedGenreIds: Array(41).fill(28) },
  }),
  JSON.stringify({
    version: 1,
    rules: { ...profileRules(ada), weeklyAllowance: 1.5 },
  }),
  JSON.stringify({
    version: 1,
    rules: { ...profileRules(ada), blockedTitles: [] },
  }),
  JSON.stringify({
    version: 1,
    rules: { ...profileRules(ada), blockedDescriptors: ['x'.repeat(101)] },
  }),
  JSON.stringify({
    version: 1,
    rules: { ...profileRules(ada), blockedTopics: Array(51).fill('dogs') },
  }),
])('rejects unsupported or malformed imports: %s', (text) => {
  expect(importRules(text)).toBeNull()
})

it.each([
  [404, 'The profile or title was not found.'],
  [500, 'The server could not complete the request. Try again.'],
  [0, 'Could not connect. Check your connection and try again.'],
  [400, 'This file was not imported.'],
])('shows the specific import error for %s', async (status, message) => {
  await open()
  const previousFetch = globalThis.fetch
  vi.stubGlobal(
    'fetch',
    vi.fn(async (path: string, init?: RequestInit) => {
      if (path.endsWith('/content-rules') && init?.method === 'PUT') {
        if (status === 0) throw new TypeError('Failed to fetch')
        return Response.json({}, { status })
      }
      return previousFetch(path, init)
    }),
  )
  await upload(exportRules(profileRules(ada), []))
  expect((await screen.findByRole('alert')).textContent).toContain(message)
})

it('rejects imports over 1 MiB locally and accepts larger than 64 KiB', async () => {
  await open()
  await upload(exportRules(profileRules(ada), []) + ' '.repeat(65536))
  await screen.findByText('Saved')
  expect(writes).toHaveLength(1)
  await upload(' '.repeat(1048577))
  expect((await screen.findByRole('alert')).textContent).toContain(
    'at most 1 MiB',
  )
  expect(writes).toHaveLength(1)
})
