import { expect, it } from 'vitest'
import { readNotices } from './notices'

it('reads a package list', () => {
  expect(
    readNotices({
      packages: [{ name: ' react ', version: '1.2.3', license: 'MIT' }],
    }),
  ).toEqual([{ name: 'react', version: '1.2.3', license: 'MIT' }])
})

it('rejects a missing, empty, or incomplete list', () => {
  expect(readNotices(null)).toBeNull()
  expect(readNotices([])).toBeNull()
  expect(readNotices({ packages: [] })).toBeNull()
  expect(readNotices({ packages: [{ name: 'react' }] })).toBeNull()
  expect(
    readNotices({
      packages: [{ name: 'react', version: '1', license: ' ' }],
    }),
  ).toBeNull()
  expect(
    readNotices({
      packages: [{ name: '', version: '1', license: 'MIT' }],
    }),
  ).toBeNull()
})
