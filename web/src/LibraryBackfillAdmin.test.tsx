import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from '@testing-library/react'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import LibraryBackfillAdmin from './LibraryBackfillAdmin'
import { StrictMode } from 'react'
import { resetAntiforgery } from './api'
import type { BackfillBatch, BackfillPreview } from './libraryBackfill'

const root = '/api/v1/labels/backfill'
let writes: { path: string; body: Record<string, unknown> }[]
let batches: BackfillBatch[]
let failed: boolean
let unavailable: boolean
let changedGuid: boolean
let reviewReason: string
let previewGate: Promise<void> | null
let firstOutcome: 'allowed' | 'needsApproval' | 'blocked'
const firstPage: BackfillPreview = {
  kidProfileId: 1,
  serverId: 'fixture-server',
  label: 'Kid',
  total: 5,
  offset: 0,
  limit: 4,
  items: [
    {
      libraryItemId: 1,
      guid: 'allowed-guid',
      title: 'Allowed film',
      kind: 'movie',
      tmdbId: 1,
      outcome: 'allowed',
      reason: { code: 'withinRatingLimit' },
      selectable: true,
    },
    {
      libraryItemId: 2,
      guid: 'review-guid',
      title: 'Review film',
      kind: 'movie',
      tmdbId: 2,
      outcome: 'needsApproval',
      reason: { code: 'unrated' },
      selectable: true,
    },
    {
      libraryItemId: 3,
      guid: 'blocked-guid',
      title: 'Blocked film',
      kind: 'movie',
      tmdbId: 3,
      outcome: 'blocked',
      reason: { code: 'aboveRatingLimit', rating: 'R' },
      selectable: true,
    },
    {
      libraryItemId: 4,
      guid: 'unmatched-guid',
      title: 'Unmatched film',
      kind: 'movie',
      tmdbId: null,
      outcome: 'blocked',
      reason: { code: 'metadataUnavailable' },
      selectable: false,
    },
  ],
}
const complete: BackfillBatch = {
  id: 9,
  kidProfileId: 1,
  label: 'Kid',
  state: 'completed',
  error: null,
  total: 2,
  pending: 0,
  applied: 2,
  preserved: 0,
  skipped: 0,
  undone: 0,
}

beforeEach(() => {
  writes = []
  batches = []
  failed = false
  unavailable = false
  changedGuid = false
  reviewReason = 'unrated'
  previewGate = null
  firstOutcome = 'allowed'
  resetAntiforgery()
  vi.stubGlobal(
    'fetch',
    vi.fn(async (path: string, init?: RequestInit) => {
      if (path === '/api/v1/auth/antiforgery')
        return Response.json({ token: 'token', headerName: 'X-CSRF-TOKEN' })
      if (path === '/api/v1/kid-profiles')
        return Response.json([
          { id: 1, name: 'Kid' },
          { id: 2, name: 'Other kid' },
        ])
      if (path.startsWith(`${root}/preview`)) {
        if (previewGate) await previewGate
        if (failed)
          return Response.json(
            { title: 'Complete a full library refresh first.' },
            { status: 409 },
          )
        if (path.endsWith('offset=4'))
          return Response.json({
            ...firstPage,
            offset: 4,
            items: [
              {
                ...firstPage.items[0],
                libraryItemId: 5,
                guid: 'page-two-guid',
                title: 'Next film',
              },
            ],
          })
        return Response.json({
          ...firstPage,
          items: firstPage.items.map((item) => {
            if (item.libraryItemId === 1)
              return {
                ...item,
                guid: changedGuid ? 'replacement-guid' : item.guid,
                selectable: !unavailable,
                outcome: unavailable ? 'blocked' : firstOutcome,
                reason: unavailable
                  ? { code: 'metadataUnavailable' }
                  : firstOutcome === 'allowed'
                    ? item.reason
                    : firstOutcome === 'needsApproval'
                      ? { code: 'unrated' }
                      : { code: 'aboveRatingLimit', rating: 'R' },
              }
            if (item.libraryItemId === 2)
              return { ...item, reason: { code: reviewReason } }
            return item
          }),
        })
      }
      if (init?.method === 'POST') {
        const body = JSON.parse(init.body as string) as Record<string, unknown>
        writes.push({ path, body })
        if (body.dryRun) return Response.json({ dryRun: true, selected: 2 })
        if (path.endsWith('/undo'))
          batches = [{ ...complete, state: 'undoing' }]
        else if (path.endsWith('/resume'))
          batches = [{ ...complete, state: 'applying' }]
        else batches = [complete]
        return Response.json(batches[0], { status: 202 })
      }
      if (path === `${root}/batches/9`)
        return Response.json({
          batch: complete,
          items: [
            {
              title: 'Allowed film',
              state: 'preserved',
              detail: 'An existing label was preserved.',
            },
          ],
        })
      if (path === `${root}/batches`) return Response.json(batches)
      throw new Error(`Unexpected request ${path}`)
    }),
  )
})

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

async function open() {
  render(
    <StrictMode>
      <LibraryBackfillAdmin />
    </StrictMode>,
  )
  fireEvent.click(
    screen.getByRole('button', { name: 'Preview library labels' }),
  )
  await screen.findByRole('option', { name: 'Kid' })
  fireEvent.change(screen.getByLabelText('Library label profile'), {
    target: { value: '1' },
  })
  await screen.findByRole('checkbox', { name: /Allowed film/ })
}

it('preselects allowed titles, explains policy, and lets the parent exclude allowed titles or explicitly include review and blocked titles', async () => {
  await open()
  const allowed = screen.getByRole('checkbox', {
    name: /Allowed film/,
  }) as HTMLInputElement
  const review = screen.getByRole('checkbox', {
    name: /Review film/,
  }) as HTMLInputElement
  const blocked = screen.getByRole('checkbox', {
    name: /Blocked film/,
  }) as HTMLInputElement
  expect(allowed.checked).toBe(true)
  expect(review.checked).toBe(false)
  expect(blocked.checked).toBe(false)
  expect(
    (
      screen.getByRole('checkbox', {
        name: /Unmatched film/,
      }) as HTMLInputElement
    ).disabled,
  ).toBe(true)
  expect(screen.getByText(/The rating R is above/)).toBeTruthy()
  fireEvent.click(allowed)
  fireEvent.click(review)
  fireEvent.click(blocked)
  fireEvent.click(screen.getByRole('button', { name: 'Apply selected labels' }))
  await waitFor(() => expect(writes).toHaveLength(1))
  expect(writes[0].body).toEqual({
    kidProfileId: 1,
    serverId: 'fixture-server',
    label: 'Kid',
    items: [
      { libraryItemId: 2, guid: 'review-guid' },
      { libraryItemId: 3, guid: 'blocked-guid' },
    ],
    dryRun: false,
  })
  await screen.findByText(/Labeling queued/)
  expect(screen.getByText(/2 applied/)).toBeTruthy()
})

it('dry runs the selected titles without clearing selection or creating a batch', async () => {
  await open()
  fireEvent.click(
    screen.getByRole('button', { name: 'Dry run selected labels' }),
  )
  await screen.findByText(/Dry run passed/)
  expect(writes).toHaveLength(1)
  expect(writes[0].body.dryRun).toBe(true)
  expect(
    (screen.getByRole('checkbox', { name: /Allowed film/ }) as HTMLInputElement)
      .checked,
  ).toBe(true)
  expect(screen.queryByRole('button', { name: 'Undo batch 9' })).toBeNull()
})

it.each(['unavailable', 'changedGuid'])(
  'removes a previously selected title after a %s refresh and never submits it',
  async (transition) => {
    await open()
    fireEvent.click(screen.getByRole('checkbox', { name: /Review film/ }))
    unavailable = transition === 'unavailable'
    changedGuid = transition === 'changedGuid'
    fireEvent.click(
      screen.getByRole('button', { name: 'Close library labels' }),
    )
    fireEvent.click(
      screen.getByRole('button', { name: 'Preview library labels' }),
    )
    await waitFor(() => {
      const refreshed = screen.getByRole('checkbox', {
        name: /Allowed film/,
      }) as HTMLInputElement
      expect(refreshed.checked).toBe(false)
      expect(refreshed.disabled).toBe(unavailable)
    })
    fireEvent.click(
      screen.getByRole('button', { name: 'Apply selected labels' }),
    )
    await waitFor(() => expect(writes).toHaveLength(1))
    expect(writes[0].body.items).toEqual([
      { libraryItemId: 2, guid: 'review-guid' },
    ])
  },
)

it.each([
  ['allowed', 'needsApproval'],
  ['allowed', 'blocked'],
  ['needsApproval', 'blocked'],
] as const)(
  'requires fresh consent when %s becomes %s without an identity change',
  async (before, after) => {
    firstOutcome = before
    await open()
    const initial = screen.getByRole('checkbox', {
      name: /Allowed film/,
    }) as HTMLInputElement
    if (!initial.checked) fireEvent.click(initial)
    fireEvent.click(screen.getByRole('checkbox', { name: /Review film/ }))
    firstOutcome = after
    fireEvent.click(
      screen.getByRole('button', { name: 'Close library labels' }),
    )
    fireEvent.click(
      screen.getByRole('button', { name: 'Preview library labels' }),
    )
    const refreshed = (await screen.findByRole('checkbox', {
      name: /Allowed film/,
    })) as HTMLInputElement
    expect(refreshed.checked).toBe(false)
    expect(
      (
        screen.getByRole('checkbox', {
          name: /Review film/,
        }) as HTMLInputElement
      ).checked,
    ).toBe(true)
    fireEvent.click(
      screen.getByRole('button', { name: 'Apply selected labels' }),
    )
    await waitFor(() => expect(writes).toHaveLength(1))
    expect(writes[0].body.items).toEqual([
      { libraryItemId: 2, guid: 'review-guid' },
    ])
    await waitFor(() =>
      expect(
        (
          screen.getByRole('checkbox', {
            name: /Review film/,
          }) as HTMLInputElement
        ).checked,
      ).toBe(false),
    )
    fireEvent.click(screen.getByRole('checkbox', { name: /Allowed film/ }))
    fireEvent.click(
      screen.getByRole('button', { name: 'Apply selected labels' }),
    )
    await waitFor(() => expect(writes).toHaveLength(2))
    expect(writes[1].body.items).toEqual([
      { libraryItemId: 1, guid: 'allowed-guid' },
    ])
  },
)

it.each(['needsApproval', 'blocked'] as const)(
  'preserves an explicit %s override when the refreshed decision is unchanged',
  async (outcome) => {
    firstOutcome = outcome
    await open()
    fireEvent.click(screen.getByRole('checkbox', { name: /Allowed film/ }))
    fireEvent.click(
      screen.getByRole('button', { name: 'Close library labels' }),
    )
    fireEvent.click(
      screen.getByRole('button', { name: 'Preview library labels' }),
    )
    const refreshed = (await screen.findByRole('checkbox', {
      name: /Allowed film/,
    })) as HTMLInputElement
    expect(refreshed.checked).toBe(true)
    fireEvent.click(
      screen.getByRole('button', { name: 'Apply selected labels' }),
    )
    await waitFor(() => expect(writes).toHaveLength(1))
    expect(writes[0].body.items).toEqual([
      { libraryItemId: 1, guid: 'allowed-guid' },
    ])
  },
)

it.each(['blocked', 'unavailable'])(
  'also deselects a previously visited copy on another page when the title becomes %s',
  async (restriction) => {
    await open()
    fireEvent.click(screen.getByRole('checkbox', { name: /Review film/ }))
    fireEvent.click(screen.getByRole('button', { name: 'Next library page' }))
    expect(
      (
        (await screen.findByRole('checkbox', {
          name: /Next film/,
        })) as HTMLInputElement
      ).checked,
    ).toBe(true)
    firstOutcome = 'blocked'
    unavailable = restriction === 'unavailable'
    fireEvent.click(
      screen.getByRole('button', { name: 'Previous library page' }),
    )
    expect(
      (
        (await screen.findByRole('checkbox', {
          name: /Allowed film/,
        })) as HTMLInputElement
      ).checked,
    ).toBe(false)
    fireEvent.click(
      screen.getByRole('button', { name: 'Apply selected labels' }),
    )
    await waitFor(() => expect(writes).toHaveLength(1))
    expect(writes[0].body.items).toEqual([
      { libraryItemId: 2, guid: 'review-guid' },
    ])
  },
)

it.each([false, true])(
  'blocks stale submission throughout reopening refresh, including failure=%s',
  async (refreshFails) => {
    await open()
    const oldApply = screen.getByRole('button', {
      name: 'Apply selected labels',
    })
    const oldDryRun = screen.getByRole('button', {
      name: 'Dry run selected labels',
    })
    let release: () => void = () => {}
    previewGate = new Promise<void>((resolve) => {
      release = resolve
    })
    failed = refreshFails
    try {
      fireEvent.click(
        screen.getByRole('button', { name: 'Close library labels' }),
      )
      fireEvent.click(
        screen.getByRole('button', { name: 'Preview library labels' }),
      )
      await screen.findByText('Checking library policy…')
      expect(
        screen.queryByRole('button', { name: 'Apply selected labels' }),
      ).toBeNull()
      expect(
        screen.queryByRole('button', { name: 'Dry run selected labels' }),
      ).toBeNull()
      fireEvent.click(oldApply)
      fireEvent.click(oldDryRun)
      expect(writes).toHaveLength(0)
      release()
      previewGate = null
      if (refreshFails) {
        await screen.findByText('Complete a full library refresh first.')
        expect(
          screen.queryByRole('button', { name: 'Apply selected labels' }),
        ).toBeNull()
        expect(
          screen.queryByRole('button', { name: 'Dry run selected labels' }),
        ).toBeNull()
        expect(writes).toHaveLength(0)
        failed = false
        fireEvent.click(
          screen.getByRole('button', { name: 'Close library labels' }),
        )
        fireEvent.click(
          screen.getByRole('button', { name: 'Preview library labels' }),
        )
      }
      fireEvent.click(
        await screen.findByRole('button', { name: 'Apply selected labels' }),
      )
      await waitFor(() => expect(writes).toHaveLength(1))
      expect(writes[0].body.items).toEqual([
        { libraryItemId: 1, guid: 'allowed-guid' },
      ])
    } finally {
      release()
    }
  },
)

it.each([
  ['unrated', 'No usable rating is available'],
  ['unknownGenres', 'Genres are unavailable'],
  ['previouslyRevoked', 'Access was revoked before'],
])('explains the specific %s review reason', async (code, explanation) => {
  reviewReason = code
  await open()
  expect(
    screen.getByRole('checkbox', { name: new RegExp(explanation) }),
  ).toBeTruthy()
})

it('preserves explicit exclusions across pages and includes selected titles from both pages', async () => {
  await open()
  fireEvent.click(screen.getByRole('checkbox', { name: /Allowed film/ }))
  fireEvent.click(screen.getByRole('checkbox', { name: /Review film/ }))
  fireEvent.click(screen.getByRole('button', { name: 'Next library page' }))
  await screen.findByRole('checkbox', { name: /Next film/ })
  fireEvent.click(screen.getByRole('button', { name: 'Previous library page' }))
  await screen.findByRole('checkbox', { name: /Allowed film/ })
  expect(
    (screen.getByRole('checkbox', { name: /Allowed film/ }) as HTMLInputElement)
      .checked,
  ).toBe(false)
  fireEvent.click(screen.getByRole('button', { name: 'Apply selected labels' }))
  await waitFor(() => expect(writes).toHaveLength(1))
  expect(writes[0].body.items).toEqual([
    { libraryItemId: 2, guid: 'review-guid' },
    { libraryItemId: 5, guid: 'page-two-guid' },
  ])
})

it('shows specific failures and supports resuming, inspecting results and undoing a saved batch', async () => {
  failed = true
  batches = [
    {
      ...complete,
      state: 'paused',
      error: 'Plex did not finish the label change.',
    },
  ]
  render(<LibraryBackfillAdmin />)
  fireEvent.click(
    screen.getByRole('button', { name: 'Preview library labels' }),
  )
  await screen.findByRole('button', { name: 'Resume batch 9' })
  fireEvent.change(screen.getByLabelText('Library label profile'), {
    target: { value: '1' },
  })
  await screen.findByText('Complete a full library refresh first.')
  fireEvent.click(screen.getByRole('button', { name: 'View batch 9 results' }))
  await screen.findByText(/An existing label was preserved/)
  fireEvent.click(screen.getByRole('button', { name: 'Resume batch 9' }))
  await waitFor(() => expect(writes[0]?.path).toBe(`${root}/batches/9/resume`))
  await waitFor(() =>
    expect(
      (
        screen.getByRole('button', {
          name: 'Undo batch 9',
        }) as HTMLButtonElement
      ).disabled,
    ).toBe(false),
  )
  fireEvent.click(screen.getByRole('button', { name: 'Undo batch 9' }))
  await waitFor(() => expect(writes[1]?.path).toBe(`${root}/batches/9/undo`))
})
