import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { axe } from '../test/axe'
import { ThemeProvider } from './ThemeProvider'
import ThemeToggle from './ThemeToggle'

function stubSystem(dark: boolean) {
  vi.stubGlobal(
    'matchMedia',
    vi.fn(() => ({
      matches: dark,
      addEventListener: () => {},
      removeEventListener: () => {},
    })),
  )
}

beforeEach(() => {
  localStorage.clear()
  delete document.documentElement.dataset.theme
})

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

const renderToggle = () =>
  render(
    <ThemeProvider>
      <ThemeToggle />
    </ThemeProvider>,
  )

it('follows the device when nothing is chosen', () => {
  stubSystem(true)
  renderToggle()
  expect(document.documentElement.dataset.theme).toBe('dark')
})

it('lets the user pick a theme and remembers it', () => {
  stubSystem(false)
  renderToggle()
  expect(document.documentElement.dataset.theme).toBe('light')
  fireEvent.change(screen.getByLabelText('Theme'), {
    target: { value: 'dark' },
  })
  expect(document.documentElement.dataset.theme).toBe('dark')
  expect(localStorage.getItem('wishlistarr.theme')).toBe('dark')
})

it('restores a saved choice', () => {
  stubSystem(false)
  localStorage.setItem('wishlistarr.theme', 'dark')
  renderToggle()
  expect(document.documentElement.dataset.theme).toBe('dark')
})

it('has no accessibility violations', async () => {
  stubSystem(false)
  const { container } = renderToggle()
  expect(await axe(container)).toHaveNoViolations()
})
