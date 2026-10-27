import { useEffect, useLayoutEffect, type RefObject } from 'react'
import {
  activateOnEnter,
  arrowMovesCaret,
  directionOf,
  focusableOrigin,
  focusMode,
  kidScaleAttribute,
  listFocusable,
  moveFocus,
  nearestIndex,
  type Box,
  type Direction,
} from './spatialNav'

type FocusMemory = {
  rect: Box
  index: number
  key: string
}

function boxOf(element: HTMLElement): Box {
  const rect = element.getBoundingClientRect()
  return {
    left: rect.left,
    top: rect.top,
    right: rect.right,
    bottom: rect.bottom,
  }
}

function focusMemoryKey(element: HTMLElement, root: HTMLElement) {
  const label =
    element.getAttribute('aria-label') ??
    element.getAttribute('aria-labelledby') ??
    element.id ??
    (element.textContent ?? '').trim()
  const parts = [label]
  let current: HTMLElement | null = element
  while (current && current !== root) {
    const parent: HTMLElement | null = current.parentElement
    const index = parent ? [...parent.children].indexOf(current) : 0
    parts.push(`${current.tagName}:${index}`)
    current = parent
  }
  return parts.join('>')
}

function rememberedBox(
  saved: FocusMemory,
  focusables: readonly HTMLElement[],
  root: HTMLElement,
) {
  const current = focusables[saved.index]
  if (current && focusMemoryKey(current, root) === saved.key) {
    return boxOf(current)
  }
  return saved.rect
}

function isRadio(target: EventTarget | null) {
  return target instanceof HTMLInputElement && target.type === 'radio'
}

function moveNearest(
  origin: Box,
  candidates: readonly HTMLElement[],
  direction: Direction,
  event: KeyboardEvent,
) {
  const index = nearestIndex(
    origin,
    candidates.map((element) => element.getBoundingClientRect()),
    direction,
  )
  const next = index === null ? null : (candidates[index] ?? null)
  if (!next) {
    if (isRadio(event.target)) event.preventDefault()
    return
  }
  event.preventDefault()
  moveFocus(next)
}

export function useKidSpatialNav(rootRef: RefObject<HTMLElement | null>) {
  useLayoutEffect(() => {
    document.documentElement.setAttribute(kidScaleAttribute, '')
    return () => {
      document.documentElement.removeAttribute(kidScaleAttribute)
    }
  }, [])

  useEffect(() => {
    let memory: FocusMemory | null = null

    const remember = (element: HTMLElement, root: HTMLElement) => {
      const focusables = listFocusable(root)
      memory = {
        rect: boxOf(element),
        index: focusables.indexOf(element),
        key: focusMemoryKey(element, root),
      }
    }

    const onFocusIn = (event: FocusEvent) => {
      const root = rootRef.current
      const target = event.target
      if (!root || !(target instanceof HTMLElement) || !root.contains(target)) {
        return
      }
      remember(focusableOrigin(target, root), root)
    }

    const onKeyDown = (event: KeyboardEvent) => {
      const root = rootRef.current
      if (!root || event.defaultPrevented) return
      if (event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) {
        return
      }

      // TV remotes move with arrows and choose with Enter or Space, so arrows do not select a radio.
      if (event.key === 'Enter') {
        if (focusMode(root, event.target) === 'move') activateOnEnter(event)
        return
      }

      const direction = directionOf(event.key)
      if (!direction) return

      const mode = focusMode(root, event.target)
      if (mode === 'ignore') return
      if (mode === 'move' && arrowMovesCaret(event.target, event.key)) return

      const focusables = listFocusable(root)
      if (mode === 'move' && event.target instanceof HTMLElement) {
        const origin = focusableOrigin(event.target, root)
        remember(origin, root)
        moveNearest(
          boxOf(origin),
          focusables.filter((element) => element !== origin),
          direction,
          event,
        )
        return
      }

      if (mode === 'enter' && memory) {
        moveNearest(
          rememberedBox(memory, focusables, root),
          focusables,
          direction,
          event,
        )
        return
      }

      if (mode !== 'enter') return
      const first = focusables[0]
      if (!first) return
      event.preventDefault()
      moveFocus(first)
    }

    document.addEventListener('focusin', onFocusIn)
    document.addEventListener('keydown', onKeyDown)
    return () => {
      document.removeEventListener('focusin', onFocusIn)
      document.removeEventListener('keydown', onKeyDown)
    }
  }, [rootRef])
}
