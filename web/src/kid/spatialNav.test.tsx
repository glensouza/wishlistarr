import { useRef, type ReactNode } from 'react'
import { cleanup, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, expect, it, vi } from 'vitest'
import {
  kidScaleAttribute,
  nearestIndex,
  spatialFocusClass,
  type Box,
} from './spatialNav'
import { useKidSpatialNav } from './useKidSpatialNav'

afterEach(() => {
  cleanup()
})

const box = (left: number, top: number, width = 100, height = 40): Box => ({
  left,
  top,
  right: left + width,
  bottom: top + height,
})

function place(
  element: HTMLElement,
  left: number,
  top: number,
  width = 160,
  height = 48,
) {
  const rect = {
    x: left,
    y: top,
    left,
    top,
    right: left + width,
    bottom: top + height,
    width,
    height,
    toJSON() {
      return rect
    },
  }
  element.getBoundingClientRect = () => rect
}

function press(element: HTMLElement, key: string) {
  const event = new KeyboardEvent('keydown', {
    key,
    bubbles: true,
    cancelable: true,
  })
  element.dispatchEvent(event)
  return event
}

function KidScreen({ children }: { children: ReactNode }) {
  const ref = useRef<HTMLDivElement>(null)
  useKidSpatialNav(ref)
  return <div ref={ref}>{children}</div>
}

it('chooses the nearest box in the arrow direction', () => {
  const visuallyRight = box(240, 0)
  const visuallyLeft = box(0, 0)
  const below = box(240, 220)
  const boxes = [visuallyRight, visuallyLeft, below]
  expect(nearestIndex(visuallyRight, boxes, 'left')).toBe(1)
  expect(nearestIndex(visuallyRight, boxes, 'right')).toBeNull()
  expect(nearestIndex(visuallyRight, boxes, 'down')).toBe(2)
  expect(nearestIndex(visuallyLeft, boxes, 'right')).toBe(0)
  expect(nearestIndex(below, boxes, 'up')).toBe(0)
})

it('prefers a control in line over a nearer one off to the side', () => {
  const origin = box(0, 0, 136, 56)
  const beside = box(168, 64, 136, 56)
  const below = box(12, 280, 136, 56)
  expect(nearestIndex(origin, [beside, below], 'down')).toBe(1)
})

it('does not treat a control tucked under the current one as being to the side', () => {
  const origin = box(40, 0, 160, 48)
  const under = box(72, 140, 160, 48)
  expect(nearestIndex(origin, [under], 'right')).toBeNull()
  expect(nearestIndex(origin, [under], 'down')).toBe(0)
})

it('skips a target that has no area', () => {
  const origin = box(0, 0, 40, 40)
  const invisible = { left: 0, top: 80, right: 0, bottom: 120 }
  const real = box(0, 200, 40, 40)
  expect(nearestIndex(origin, [invisible, real], 'down')).toBe(1)
})

it('moves by row when buttons in a row sit at different heights', () => {
  render(
    <KidScreen>
      <button type="button">Tiny</button>
      <button type="button">Bluey</button>
      <button type="button">Tiny again</button>
      <button type="button">Bluey again</button>
    </KidScreen>,
  )
  const topTiny = screen.getByRole('button', { name: 'Tiny' })
  const topBluey = screen.getByRole('button', { name: 'Bluey' })
  const lowerTiny = screen.getByRole('button', { name: 'Tiny again' })
  const lowerBluey = screen.getByRole('button', { name: 'Bluey again' })
  place(topTiny, 0, 0, 136, 56)
  place(topBluey, 188, 44, 136, 56)
  place(lowerTiny, 24, 340, 136, 56)
  place(lowerBluey, 212, 384, 136, 56)

  topTiny.focus()
  press(topTiny, 'ArrowRight')
  expect(document.activeElement).toBe(topBluey)
  press(topBluey, 'ArrowLeft')
  expect(document.activeElement).toBe(topTiny)
  press(topTiny, 'ArrowDown')
  expect(document.activeElement).toBe(lowerTiny)
  press(lowerTiny, 'ArrowUp')
  expect(document.activeElement).toBe(topTiny)
  topBluey.focus()
  press(topBluey, 'ArrowDown')
  expect(document.activeElement).toBe(lowerBluey)
  press(lowerBluey, 'ArrowUp')
  expect(document.activeElement).toBe(topBluey)
  press(topBluey, 'ArrowRight')
  expect(document.activeElement).toBe(topBluey)
  topTiny.focus()
  press(topTiny, 'ArrowLeft')
  expect(document.activeElement).toBe(topTiny)
})

it('moves to the spatially nearest control when that control comes later in the page', () => {
  render(
    <KidScreen>
      <button type="button">Right</button>
      <button type="button">Left</button>
    </KidScreen>,
  )
  const right = screen.getByRole('button', { name: 'Right' })
  const left = screen.getByRole('button', { name: 'Left' })
  place(right, 240, 0)
  place(left, 0, 0)
  right.focus()
  press(right, 'ArrowLeft')
  expect(document.activeElement).toBe(left)
  expect(left.classList.contains(spatialFocusClass)).toBe(true)
  expect(right.classList.contains(spatialFocusClass)).toBe(false)
  press(left, 'ArrowRight')
  expect(document.activeElement).toBe(right)
})

it('checks a radio with Enter after the arrows have only moved focus', () => {
  render(
    <KidScreen>
      <label>
        <input type="radio" name="season" value="1" />
        Season 1
      </label>
      <label>
        <input type="radio" name="season" value="2" />
        Season 2
      </label>
    </KidScreen>,
  )
  const [first, second] = screen.getAllByRole('radio') as HTMLInputElement[]
  place(first, 0, 0)
  place(second, 180, 0)
  first.focus()
  press(first, 'ArrowRight')
  expect(document.activeElement).toBe(second)
  expect(first.checked).toBe(false)
  expect(second.checked).toBe(false)
  press(second, 'Enter')
  expect(second.checked).toBe(true)
  expect(first.checked).toBe(false)
})

it('activates a button once when Enter is pressed', async () => {
  const onAsk = vi.fn()
  render(
    <KidScreen>
      <button type="button" onClick={onAsk}>
        Ask
      </button>
    </KidScreen>,
  )
  const ask = screen.getByRole('button', { name: 'Ask' })
  place(ask, 0, 0)
  ask.focus()
  await userEvent.setup().keyboard('{Enter}')
  expect(onAsk).toHaveBeenCalledTimes(1)
})

it('lets horizontal arrows move the caret and vertical arrows leave a text field', () => {
  render(
    <KidScreen>
      <input type="search" aria-label="Search movies and shows" />
      <button type="button">Ask</button>
    </KidScreen>,
  )
  const search = screen.getByRole('searchbox', {
    name: 'Search movies and shows',
  })
  const ask = screen.getByRole('button', { name: 'Ask' })
  place(search, 0, 0, 240, 48)
  place(ask, 280, 0)
  search.focus()
  const right = press(search, 'ArrowRight')
  expect(right.defaultPrevented).toBe(false)
  expect(document.activeElement).toBe(search)
  place(ask, 0, 120)
  const down = press(search, 'ArrowDown')
  expect(down.defaultPrevented).toBe(true)
  expect(document.activeElement).toBe(ask)
})

it('does not take arrow keys from controls outside the kid screen', () => {
  render(
    <>
      <button type="button">Parents</button>
      <select aria-label="Theme" defaultValue="light">
        <option value="light">Light</option>
        <option value="dark">Dark</option>
      </select>
      <KidScreen>
        <button type="button">Ada</button>
      </KidScreen>
    </>,
  )
  const parents = screen.getByRole('button', { name: 'Parents' })
  const theme = screen.getByRole('combobox', { name: 'Theme' })
  const ada = screen.getByRole('button', { name: 'Ada' })
  place(parents, 0, 0)
  place(theme, 180, 0)
  place(ada, 0, 120)
  parents.focus()
  press(parents, 'ArrowDown')
  expect(document.activeElement).toBe(parents)
  theme.focus()
  press(theme, 'ArrowDown')
  expect(document.activeElement).toBe(theme)
})

it('starts at the first kid control when nothing on the screen has focus', () => {
  render(
    <>
      <button type="button">Parents</button>
      <KidScreen>
        <button type="button">Ada</button>
        <button type="button">Bo</button>
      </KidScreen>
    </>,
  )
  const parents = screen.getByRole('button', { name: 'Parents' })
  const ada = screen.getByRole('button', { name: 'Ada' })
  const bo = screen.getByRole('button', { name: 'Bo' })
  place(parents, 0, 0)
  place(ada, 0, 80)
  place(bo, 200, 80)
  const event = press(document.body, 'ArrowDown')
  expect(event.defaultPrevented).toBe(true)
  expect(document.activeElement).toBe(ada)
})

it('marks a mounted kid screen and clears the mark when it goes away', () => {
  const view = render(
    <KidScreen>
      <button type="button">Ada</button>
    </KidScreen>,
  )
  expect(document.documentElement.hasAttribute(kidScaleAttribute)).toBe(true)
  view.unmount()
  expect(document.documentElement.hasAttribute(kidScaleAttribute)).toBe(false)
})
