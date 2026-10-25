import { readFileSync, readdirSync } from 'node:fs'
import { join } from 'node:path'
import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, expect, it } from 'vitest'
import { WatchProviders } from './WatchProviders'

afterEach(cleanup)

function tsxFiles(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const path = join(dir, entry.name)
    if (entry.isDirectory()) return tsxFiles(path)
    return entry.name.endsWith('.tsx') && !entry.name.includes('.test.')
      ? [path]
      : []
  })
}

it('credits JustWatch in the same section as the provider names', () => {
  render(
    <WatchProviders providers={[{ name: 'Stream' }, { name: ' RentCo ' }]} />,
  )
  const stream = screen.getByText('Stream')
  const credit = screen.getByText('Watch provider data from JustWatch')
  expect(stream.closest('section')).toBe(credit.closest('section'))
  expect(screen.queryByRole('link')).toBeNull()
  expect(screen.getByText('RentCo')).toBeTruthy()
  expect(screen.getByRole('region', { name: 'Watch providers' })).toBeTruthy()
})

it('shows nothing when there is no provider data', () => {
  const empty = render(<WatchProviders providers={[]} />)
  expect(empty.container.textContent).toBe('')
  expect(empty.container.querySelector('a')).toBeNull()
  empty.unmount()

  const blank = render(
    <WatchProviders providers={[{ name: ' ' }, { name: '' }]} />,
  )
  expect(blank.container.textContent).toBe('')
  expect(blank.container.querySelector('a')).toBeNull()
})

const providerData =
  /watch[_-]?providers?|provider_?(?:name|id)|display_?priority|logo_?path|flatrate|\bjustwatch\b/i

it('requires the JustWatch credit wherever provider data is rendered', () => {
  const watch = readFileSync(join(__dirname, 'WatchProviders.tsx'), 'utf8')
  expect(watch).toContain('<WatchProviderCredit')
  for (const file of tsxFiles(join(__dirname, '..'))) {
    if (/(?:WatchProviders|About)\.tsx$/.test(file)) continue
    const text = readFileSync(file, 'utf8')
    if (providerData.test(text)) {
      expect(text, file).toMatch(/<WatchProviders\b|<WatchProviderCredit\b/)
    }
  }
})
