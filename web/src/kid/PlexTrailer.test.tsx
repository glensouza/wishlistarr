import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, expect, it } from 'vitest'
import PlexTrailer from './PlexTrailer'

afterEach(cleanup)

it('loads the local video only after play and restores focus on close', async () => {
  const user = userEvent.setup()
  const { container } = render(
    <PlexTrailer
      url="/api/v1/kid/titles/movie/800/trailer"
      backdropUrl={null}
    />,
  )
  expect(container.querySelector('video')).toBeNull()
  const play = screen.getByRole('button', { name: 'Play trailer' })
  await user.click(play)
  expect(container.querySelector('video')?.getAttribute('src')).toBe(
    '/api/v1/kid/titles/movie/800/trailer',
  )
  const close = screen.getByRole('button', { name: 'Close trailer' })
  await waitFor(() => expect(document.activeElement).toBe(close))
  fireEvent.keyDown(close, { key: 'Escape' })
  expect(container.querySelector('video')).toBeNull()
  await waitFor(() =>
    expect(document.activeElement).toBe(
      screen.getByRole('button', { name: 'Play trailer' }),
    ),
  )
})

it('does not play a remote URL', () => {
  const { container } = render(
    <PlexTrailer url="https://example.invalid/video.mp4" backdropUrl={null} />,
  )
  expect(screen.queryByRole('button', { name: 'Play trailer' })).toBeNull()
  expect(container.querySelector('video')).toBeNull()
})
