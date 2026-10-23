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
  const credit = screen.getByRole('link', { name: 'JustWatch' })
  expect(credit.getAttribute('href')).toBe('https://www.justwatch.com/')
  expect(stream.closest('section')).toBe(credit.closest('section'))
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

it('requires the JustWatch credit wherever provider fields are rendered', () => {
  const watch = readFileSync(join(__dirname, 'WatchProviders.tsx'), 'utf8')
  expect(watch).toContain('<WatchProviderCredit')
  for (const file of tsxFiles(join(__dirname, '..'))) {
    if (file.endsWith('WatchProviders.tsx')) continue
    const text = readFileSync(file, 'utf8')
    if (/providerName|provider_name|flatrate/.test(text)) {
      expect(text, file).toContain('<WatchProviderCredit')
    }
  }
})
