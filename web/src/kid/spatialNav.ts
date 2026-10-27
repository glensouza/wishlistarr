export const spatialFocusClass = 'kid-spatial-focus'
export const kidScaleAttribute = 'data-kid-scale'

export type Box = {
  left: number
  top: number
  right: number
  bottom: number
}

export type Direction = 'left' | 'right' | 'up' | 'down'

// One pixel of overlap is rounding, not a step into the next row or column.
const directionSlop = 1

const axis: Record<Direction, { horizontal: boolean; sign: 1 | -1 }> = {
  left: { horizontal: true, sign: -1 },
  right: { horizontal: true, sign: 1 },
  up: { horizontal: false, sign: -1 },
  down: { horizontal: false, sign: 1 },
}

const directions: Record<string, Direction> = {
  ArrowLeft: 'left',
  ArrowRight: 'right',
  ArrowUp: 'up',
  ArrowDown: 'down',
}

const focusableSelector = [
  'a[href]',
  'button:not([disabled])',
  'input:not([disabled]):not([type="hidden"])',
  'select:not([disabled])',
  'textarea:not([disabled])',
  '[tabindex]:not([tabindex="-1"])',
].join(',')

const horizontalCaret = new Set([
  'text',
  'search',
  'email',
  'password',
  'url',
  'tel',
])

const valueCaret = new Set([
  'number',
  'range',
  'date',
  'time',
  'datetime-local',
  'month',
  'week',
])

export function directionOf(key: string): Direction | null {
  return directions[key] ?? null
}

function crossOverlap(origin: Box, box: Box, horizontal: boolean) {
  if (horizontal) {
    return origin.top < box.bottom && box.top < origin.bottom
  }
  return origin.left < box.right && box.left < origin.right
}

export function nearestIndex(
  origin: Box,
  boxes: readonly Box[],
  direction: Direction,
): number | null {
  const { horizontal, sign } = axis[direction]
  const originCross =
    (horizontal ? origin.top + origin.bottom : origin.left + origin.right) / 2
  let aligned: number | null = null
  let alignedTravel = Number.POSITIVE_INFINITY
  let alignedDrift = Number.POSITIVE_INFINITY
  let fallback: number | null = null
  let fallbackScore = Number.POSITIVE_INFINITY

  boxes.forEach((box, index) => {
    if (box.right - box.left <= 0 || box.bottom - box.top <= 0) return
    const primary = horizontal
      ? sign === 1
        ? box.left - origin.right
        : origin.left - box.right
      : sign === 1
        ? box.top - origin.bottom
        : origin.top - box.bottom
    if (primary < -directionSlop) return

    const travel = Math.max(primary, 0)
    const boxCross =
      (horizontal ? box.top + box.bottom : box.left + box.right) / 2
    const secondary = boxCross - originCross
    if (crossOverlap(origin, box, horizontal)) {
      const drift = Math.abs(secondary)
      if (
        travel < alignedTravel ||
        (travel === alignedTravel && drift < alignedDrift)
      ) {
        alignedTravel = travel
        alignedDrift = drift
        aligned = index
      }
      return
    }

    const score = travel * travel + secondary * secondary
    if (score < fallbackScore) {
      fallbackScore = score
      fallback = index
    }
  })

  return aligned ?? fallback
}

export function arrowMovesCaret(
  target: EventTarget | null,
  key: string,
): boolean {
  if (!(target instanceof HTMLElement)) return false
  if (target.isContentEditable || target instanceof HTMLTextAreaElement) {
    return true
  }
  if (target instanceof HTMLSelectElement) return true
  if (!(target instanceof HTMLInputElement)) return false
  if (valueCaret.has(target.type)) return true
  if (!horizontalCaret.has(target.type)) return false
  return key === 'ArrowLeft' || key === 'ArrowRight'
}

export function focusMode(
  root: HTMLElement,
  target: EventTarget | null,
): 'move' | 'enter' | 'ignore' {
  if (!(target instanceof HTMLElement)) return 'ignore'
  if (root.contains(target)) return 'move'
  if (
    target === document.body ||
    target === document.documentElement ||
    target.contains(root)
  ) {
    return 'enter'
  }
  return 'ignore'
}

export function listFocusable(root: HTMLElement): HTMLElement[] {
  return [...root.querySelectorAll<HTMLElement>(focusableSelector)].filter(
    (element) => {
      if (element.matches(':disabled')) return false
      if (element.closest('[hidden], [inert], [aria-hidden="true"]')) {
        return false
      }
      const box = element.getBoundingClientRect()
      return box.width > 0 && box.height > 0
    },
  )
}

export function focusableOrigin(
  target: HTMLElement,
  root: HTMLElement,
): HTMLElement {
  const match = target.closest<HTMLElement>(focusableSelector)
  if (
    match &&
    root.contains(match) &&
    !match.matches(':disabled') &&
    match.getAttribute('tabindex') !== '-1'
  ) {
    return match
  }
  return target
}

export function nearestElement(
  origin: HTMLElement,
  candidates: readonly HTMLElement[],
  direction: Direction,
): HTMLElement | null {
  const index = nearestIndex(
    origin.getBoundingClientRect(),
    candidates.map((element) => element.getBoundingClientRect()),
    direction,
  )
  return index === null ? null : (candidates[index] ?? null)
}

export function moveFocus(element: HTMLElement) {
  document.querySelectorAll(`.${spatialFocusClass}`).forEach((node) => {
    node.classList.remove(spatialFocusClass)
  })
  element.classList.add(spatialFocusClass)
  element.addEventListener(
    'blur',
    () => element.classList.remove(spatialFocusClass),
    { once: true },
  )
  element.focus({ preventScroll: true, focusVisible: true })
  if (typeof element.scrollIntoView === 'function') {
    element.scrollIntoView({ block: 'nearest', inline: 'nearest' })
  }
}

export function activateOnEnter(event: KeyboardEvent) {
  if (event.repeat) return
  const target = event.target
  if (!(target instanceof HTMLInputElement) || target.disabled) return
  if (target.type !== 'radio' && target.type !== 'checkbox') return
  event.preventDefault()
  target.click()
}
