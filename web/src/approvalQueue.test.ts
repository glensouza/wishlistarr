import { expect, it } from 'vitest'
import { QueueShapeError, readQueue } from './approvalQueue'

const item = {
  id: 1,
  kidProfileId: 10,
  kidName: 'Ada',
  kind: 'movie',
  tmdbId: 603,
  title: 'The Matrix',
  requestCount: 1,
  createdAt: '2026-01-02T00:00:00.000Z',
  overview: 'A hacker learns the truth.',
  rating: 'PG-13',
  descriptors: [],
  providers: [],
  trailerKey: null,
  reason: {
    code: 'unavailable',
    rating: null,
    level: null,
    maxLevel: null,
    genreName: null,
    keywordName: null,
    language: null,
    descriptor: null,
    topic: null,
  },
  contentWarnings: null,
  attribution: null,
  parentNote: null,
}

function queue(gaps: string[]) {
  return { items: [{ ...item, gaps }], total: 1, limit: 50, offset: 0 }
}

it('skips a gap name it does not know and still reads the page', () => {
  expect(readQueue(queue(['synopsis', 'posters'])).items[0].gaps).toEqual([
    'synopsis',
  ])
})

it('reads a keyword gap', () => {
  expect(readQueue(queue(['keywords'])).items[0].gaps).toEqual(['keywords'])
})

it('treats a missing TMDB flag as configured', () => {
  expect(readQueue(queue([])).tmdbConfigured).toBe(true)
})

it('reads the TMDB flag when the server sends it', () => {
  expect(
    readQueue({ ...queue([]), tmdbConfigured: false }).tmdbConfigured,
  ).toBe(false)
  expect(readQueue({ ...queue([]), tmdbConfigured: true }).tmdbConfigured).toBe(
    true,
  )
})

it('rejects a TMDB flag that is not true or false', () => {
  expect(() => readQueue({ ...queue([]), tmdbConfigured: 'no' })).toThrow(
    QueueShapeError,
  )
})

it('reads warning names, attribution and the shared parent note', () => {
  const page = readQueue({
    items: [
      {
        ...item,
        gaps: ['contentWarnings', 'posters'],
        contentWarnings: ['a dog dies'],
        attribution: {
          text: 'Powered by DoesTheDogDie.com',
          url: 'https://www.doesthedogdie.com',
        },
        parentNote: 'Shared',
      },
    ],
    total: 1,
    limit: 50,
    offset: 0,
  })
  expect(page.items[0].gaps).toEqual(['contentWarnings'])
  expect(page.items[0].contentWarnings).toEqual(['a dog dies'])
  expect(page.items[0].attribution).toEqual({
    text: 'Powered by DoesTheDogDie.com',
    url: 'https://www.doesthedogdie.com',
  })
  expect(page.items[0].parentNote).toBe('Shared')
})

it('rejects an item that omits the warning fields', () => {
  const { contentWarnings, attribution, parentNote, ...rest } = item
  expect(contentWarnings).toBeNull()
  expect(attribution).toBeNull()
  expect(parentNote).toBeNull()
  expect(() =>
    readQueue({
      items: [{ ...rest, gaps: [] }],
      total: 1,
      limit: 50,
      offset: 0,
    }),
  ).toThrow(QueueShapeError)
})
