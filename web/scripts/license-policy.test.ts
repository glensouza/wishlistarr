import { expect, it } from 'vitest'
import { licenseAllowed } from './license-policy.mjs'

const allowed = ['MIT', 'Apache-2.0', 'OFL-1.1']

it('accepts an OR expression when any side is allowed', () => {
  expect(licenseAllowed('(MIT OR GPL-2.0-only)', allowed)).toBe(true)
  expect(licenseAllowed('GPL-2.0-only OR Apache-2.0', allowed)).toBe(true)
  expect(licenseAllowed('MIT or Apache-2.0', ['Apache-2.0'])).toBe(true)
})

it('rejects an AND expression when any side is not allowed', () => {
  expect(licenseAllowed('MIT AND Apache-2.0', allowed)).toBe(true)
  expect(licenseAllowed('(MIT AND GPL-2.0-only)', allowed)).toBe(false)
  expect(licenseAllowed('GPL-2.0-only AND MIT', allowed)).toBe(false)
})

it('rejects a WITH expression unless the whole exception is listed', () => {
  expect(
    licenseAllowed('GPL-2.0-only WITH Classpath-exception-2.0', allowed),
  ).toBe(false)
  expect(licenseAllowed('MIT WITH X', ['MIT'])).toBe(false)
  expect(licenseAllowed('MIT WITH X', ['MIT WITH X'])).toBe(true)
})

it('rejects unknown, unlicensed, guessed, and empty expressions', () => {
  for (const expression of [
    '',
    '   ',
    'UNKNOWN',
    'UNLICENSED',
    'MIT*',
    '*',
    'SEE LICENSE IN LICENSE',
    'mit',
  ]) {
    expect(licenseAllowed(expression, allowed), expression).toBe(false)
  }
  expect(licenseAllowed(undefined, allowed)).toBe(false)
})
