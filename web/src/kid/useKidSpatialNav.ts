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
  nearestElement,
} from './spatialNav'

export function useKidSpatialNav(rootRef: RefObject<HTMLElement | null>) {
  useLayoutEffect(() => {
    document.documentElement.setAttribute(kidScaleAttribute, '')
    return () => {
      document.documentElement.removeAttribute(kidScaleAttribute)
    }
  }, [])

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      const root = rootRef.current
      if (!root || event.defaultPrevented) return
      if (event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) {
        return
      }

      if (event.key === 'Enter') {
        activateOnEnter(event)
        return
      }

      const direction = directionOf(event.key)
      if (!direction) return

      const mode = focusMode(root, event.target)
      if (mode === 'ignore') return
      if (mode === 'move' && arrowMovesCaret(event.target, event.key)) return

      const focusables = listFocusable(root)
      if (mode === 'enter') {
        const first = focusables[0]
        if (!first) return
        event.preventDefault()
        moveFocus(first)
        return
      }

      if (!(event.target instanceof HTMLElement)) return
      const origin = focusableOrigin(event.target, root)
      const next = nearestElement(
        origin,
        focusables.filter((element) => element !== origin),
        direction,
      )
      event.preventDefault()
      if (next) moveFocus(next)
    }

    document.addEventListener('keydown', onKeyDown)
    return () => document.removeEventListener('keydown', onKeyDown)
  }, [rootRef])
}
