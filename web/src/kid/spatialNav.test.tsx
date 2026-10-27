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

it('leaves the arrow key alone at the edge and cancels it when focus moves', () => {
  render(
    <KidScreen>
      <button type="button">Only</button>
      <button type="button">Beside</button>
    </KidScreen>,
  )
  const only = screen.getByRole('button', { name: 'Only' })
  const beside = screen.getByRole('button', { name: 'Beside' })
  place(only, 0, 80)
  place(beside, 220, 80)
  only.focus()
  const down = press(only, 'ArrowDown')
  expect(down.defaultPrevented).toBe(false)
  expect(document.activeElement).toBe(only)
  const right = press(only, 'ArrowRight')
  expect(right.defaultPrevented).toBe(true)
  expect(document.activeElement).toBe(beside)
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

it('continues from a control that can no longer take focus', () => {
  const view = render(
    <KidScreen>
      <button type="button">Earlier</button>
      <button type="button">Ask</button>
      <button type="button">Below</button>
    </KidScreen>,
  )
  const earlier = screen.getByRole('button', { name: 'Earlier' })
  const ask = screen.getByRole('button', { name: 'Ask' })
  const below = screen.getByRole('button', { name: 'Below' })
  place(earlier, 420, 0, 140, 48)
  place(ask, 0, 80, 140, 56)
  place(below, 0, 240, 140, 56)
  ask.focus()
  view.rerender(
    <KidScreen>
      <button type="button">Earlier</button>
      <button type="button" disabled>
        Ask
      </button>
      <button type="button">Below</button>
    </KidScreen>,
  )
  const earlierAfter = screen.getByRole('button', { name: 'Earlier' })
  const belowAfter = screen.getByRole('button', { name: 'Below' })
  place(earlierAfter, 420, 0, 140, 48)
  place(belowAfter, 0, 240, 140, 56)
  const focused = document.activeElement
  if (focused instanceof HTMLElement && focused !== document.body) {
    focused.blur()
  }
  const down = press(document.body, 'ArrowDown')
  expect(down.defaultPrevented).toBe(true)
  expect(document.activeElement).toBe(belowAfter)
})

it('uses where a control is now when focus has left it but it is still there', () => {
  render(
    <KidScreen>
      <button type="button">Earlier</button>
      <button type="button">Ask</button>
      <button type="button">Below</button>
    </KidScreen>,
  )
  const earlier = screen.getByRole('button', { name: 'Earlier' })
  const ask = screen.getByRole('button', { name: 'Ask' })
  const below = screen.getByRole('button', { name: 'Below' })
  place(earlier, 0, 200, 140, 48)
  place(ask, 0, 0, 140, 48)
  place(below, 0, 400, 140, 48)
  ask.focus()
  ask.blur()
  expect(document.activeElement).toBe(document.body)
  place(ask, 400, 40, 140, 48)
  place(below, 400, 200, 140, 48)
  place(earlier, 0, 200, 140, 48)
  press(document.body, 'ArrowDown')
  expect(document.activeElement).toBe(below)
})

it('does not choose a checkbox outside the kid screen', () => {
  render(
    <>
      <label>
        Outside
        <input type="checkbox" />
      </label>
      <KidScreen>
        <label>
          Inside
          <input type="checkbox" />
        </label>
      </KidScreen>
    </>,
  )
  const [outside, inside] = screen.getAllByRole(
    'checkbox',
  ) as HTMLInputElement[]
  outside.focus()
  press(outside, 'Enter')
  expect(outside.checked).toBe(false)
  inside.focus()
  press(inside, 'Enter')
  expect(inside.checked).toBe(true)
})

function placeAround(field: HTMLElement) {
  const above = screen.getByRole('button', { name: 'Above' })
  const before = screen.getByRole('button', { name: 'Before' })
  const after = screen.getByRole('button', { name: 'After' })
  const below = screen.getByRole('button', { name: 'Below' })
  place(above, 80, 0, 160, 48)
  place(before, 0, 80, 72, 48)
  place(field, 88, 80, 200, 48)
  place(after, 304, 80, 72, 48)
  place(below, 80, 160, 160, 48)
  return { above, before, after, below }
}

it('lets a textarea keep arrows inside the text and leave at an edge', () => {
  render(
    <KidScreen>
      <button type="button">Above</button>
      <button type="button">Before</button>
      <textarea aria-label="Note" defaultValue={'cat\ndog'} />
      <button type="button">After</button>
      <button type="button">Below</button>
    </KidScreen>,
  )
  const note = screen.getByRole('textbox', {
    name: 'Note',
  }) as HTMLTextAreaElement
  const { above, before, after, below } = placeAround(note)
  note.focus()
  note.setSelectionRange(1, 1)
  expect(press(note, 'ArrowUp').defaultPrevented).toBe(true)
  expect(document.activeElement).toBe(above)
  note.focus()
  note.setSelectionRange(1, 1)
  expect(press(note, 'ArrowDown').defaultPrevented).toBe(false)
  expect(document.activeElement).toBe(note)
  expect(press(note, 'ArrowLeft').defaultPrevented).toBe(false)
  expect(document.activeElement).toBe(note)
  note.setSelectionRange(0, 0)
  expect(press(note, 'ArrowLeft').defaultPrevented).toBe(true)
  expect(document.activeElement).toBe(before)
  note.focus()
  note.setSelectionRange(7, 7)
  expect(press(note, 'ArrowRight').defaultPrevented).toBe(true)
  expect(document.activeElement).toBe(after)
  note.focus()
  note.setSelectionRange(5, 5)
  expect(press(note, 'ArrowDown').defaultPrevented).toBe(true)
  expect(document.activeElement).toBe(below)
})

it('lets a closed list keep up and down until the end and leaves sideways', () => {
  render(
    <KidScreen>
      <button type="button">Above</button>
      <button type="button">Before</button>
      <select aria-label="Theme" defaultValue="dark">
        <option value="light">Light</option>
        <option value="dark">Dark</option>
        <option value="night">Night</option>
      </select>
      <button type="button">After</button>
      <button type="button">Below</button>
    </KidScreen>,
  )
  const theme = screen.getByRole('combobox', {
    name: 'Theme',
  }) as HTMLSelectElement
  const { above, before, after, below } = placeAround(theme)
  theme.focus()
  expect(press(theme, 'ArrowUp').defaultPrevented).toBe(false)
  expect(document.activeElement).toBe(theme)
  expect(press(theme, 'ArrowDown').defaultPrevented).toBe(false)
  expect(document.activeElement).toBe(theme)
  expect(press(theme, 'ArrowLeft').defaultPrevented).toBe(true)
  expect(document.activeElement).toBe(before)
  theme.focus()
  expect(press(theme, 'ArrowRight').defaultPrevented).toBe(true)
  expect(document.activeElement).toBe(after)
  theme.focus()
  theme.selectedIndex = 0
  expect(press(theme, 'ArrowUp').defaultPrevented).toBe(true)
  expect(document.activeElement).toBe(above)
  theme.focus()
  theme.selectedIndex = theme.options.length - 1
  expect(press(theme, 'ArrowDown').defaultPrevented).toBe(true)
  expect(document.activeElement).toBe(below)
})

it('lets a slider keep left and right until the end and leaves on the other axis', () => {
  render(
    <KidScreen>
      <button type="button">Above</button>
      <button type="button">Before</button>
      <input
        type="range"
        aria-label="Volume"
        min={0}
        max={10}
        defaultValue={4}
      />
      <button type="button">After</button>
      <button type="button">Below</button>
    </KidScreen>,
  )
  const volume = screen.getByRole('slider', {
    name: 'Volume',
  }) as HTMLInputElement
  const { above, before, after, below } = placeAround(volume)
  volume.focus()
  expect(press(volume, 'ArrowLeft').defaultPrevented).toBe(false)
  expect(document.activeElement).toBe(volume)
  expect(press(volume, 'ArrowRight').defaultPrevented).toBe(false)
  expect(document.activeElement).toBe(volume)
  expect(press(volume, 'ArrowUp').defaultPrevented).toBe(true)
  expect(document.activeElement).toBe(above)
  volume.focus()
  expect(press(volume, 'ArrowDown').defaultPrevented).toBe(true)
  expect(document.activeElement).toBe(below)
  volume.focus()
  volume.value = '0'
  expect(press(volume, 'ArrowLeft').defaultPrevented).toBe(true)
  expect(document.activeElement).toBe(before)
  volume.focus()
  volume.value = '10'
  expect(press(volume, 'ArrowRight').defaultPrevented).toBe(true)
  expect(document.activeElement).toBe(after)
})

it('lets a number field keep up and down until the limit and leaves sideways', () => {
  render(
    <KidScreen>
      <button type="button">Above</button>
      <button type="button">Before</button>
      <input
        type="number"
        aria-label="Count"
        min={1}
        max={5}
        defaultValue={3}
      />
      <button type="button">After</button>
      <button type="button">Below</button>
    </KidScreen>,
  )
  const count = screen.getByRole('spinbutton', {
    name: 'Count',
  }) as HTMLInputElement
  const { above, before, after, below } = placeAround(count)
  count.focus()
  expect(press(count, 'ArrowUp').defaultPrevented).toBe(false)
  expect(document.activeElement).toBe(count)
  expect(press(count, 'ArrowDown').defaultPrevented).toBe(false)
  expect(document.activeElement).toBe(count)
  expect(press(count, 'ArrowLeft').defaultPrevented).toBe(true)
  expect(document.activeElement).toBe(before)
  count.focus()
  expect(press(count, 'ArrowRight').defaultPrevented).toBe(true)
  expect(document.activeElement).toBe(after)
  count.focus()
  count.value = '5'
  expect(press(count, 'ArrowUp').defaultPrevented).toBe(true)
  expect(document.activeElement).toBe(above)
  count.focus()
  count.value = '1'
  expect(press(count, 'ArrowDown').defaultPrevented).toBe(true)
  expect(document.activeElement).toBe(below)
})

it.each([
  ['date', '2020-06-15', '2020-01-01', '2020-12-31'],
  ['time', '12:00', '08:00', '18:00'],
  [
    'datetime-local',
    '2020-05-01T12:00',
    '2020-05-01T08:00',
    '2020-05-01T18:00',
  ],
  ['month', '2020-06', '2020-01', '2020-12'],
  ['week', '2020-W20', '2020-W01', '2020-W52'],
])(
  'lets a %s field keep up and down until the limit and leaves sideways',
  (type, value, min, max) => {
    render(
      <KidScreen>
        <button type="button">Above</button>
        <button type="button">Before</button>
        <input
          type={type}
          aria-label="When"
          min={min}
          max={max}
          defaultValue={value}
        />
        <button type="button">After</button>
        <button type="button">Below</button>
      </KidScreen>,
    )
    const when = screen.getByLabelText('When') as HTMLInputElement
    const { above, before, after, below } = placeAround(when)
    when.focus()
    expect(press(when, 'ArrowUp').defaultPrevented).toBe(false)
    expect(document.activeElement).toBe(when)
    expect(press(when, 'ArrowDown').defaultPrevented).toBe(false)
    expect(document.activeElement).toBe(when)
    expect(press(when, 'ArrowLeft').defaultPrevented).toBe(true)
    expect(document.activeElement).toBe(before)
    when.focus()
    expect(press(when, 'ArrowRight').defaultPrevented).toBe(true)
    expect(document.activeElement).toBe(after)
    when.focus()
    when.value = max
    expect(press(when, 'ArrowUp').defaultPrevented).toBe(true)
    expect(document.activeElement).toBe(above)
    when.focus()
    when.value = min
    expect(press(when, 'ArrowDown').defaultPrevented).toBe(true)
    expect(document.activeElement).toBe(below)
  },
)

it.each(['text', 'email', 'password', 'url', 'tel'])(
  'keeps sideways arrows inside a %s field and leaves on the other axis',
  (type) => {
    render(
      <KidScreen>
        <button type="button">Above</button>
        <input type={type} aria-label="Value" defaultValue="hello" />
        <button type="button">Below</button>
      </KidScreen>,
    )
    const field = screen.getByLabelText('Value')
    const below = screen.getByRole('button', { name: 'Below' })
    place(screen.getByRole('button', { name: 'Above' }), 0, 0)
    place(field, 0, 80, 200, 48)
    place(below, 0, 160)
    field.focus()
    expect(press(field, 'ArrowRight').defaultPrevented).toBe(false)
    expect(document.activeElement).toBe(field)
    expect(press(field, 'ArrowLeft').defaultPrevented).toBe(false)
    expect(document.activeElement).toBe(field)
    expect(press(field, 'ArrowDown').defaultPrevented).toBe(true)
    expect(document.activeElement).toBe(below)
  },
)

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
