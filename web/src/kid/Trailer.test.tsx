import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, expect, it, vi } from 'vitest'
import Trailer, { youtubeApiUrl } from './Trailer'

type PlayerOptions = ConstructorParameters<
  NonNullable<Window['YT']>['Player']
>[1]

type User = ReturnType<typeof userEvent.setup>

class FakePlayer {
  static created = 0
  static last: FakePlayer | null = null
  static constructorArgs: unknown[] = []
  options: PlayerOptions
  element: HTMLElement
  played = false
  destroyed = false

  constructor(element: HTMLElement, options: PlayerOptions) {
    FakePlayer.created += 1
    FakePlayer.last = this
    FakePlayer.constructorArgs = [element, options]
    this.element = element
    this.options = options
    queueMicrotask(() => {
      if (!this.destroyed) options.events.onReady({ target: this })
    })
  }

  playVideo() {
    this.played = true
  }

  destroy() {
    this.destroyed = true
    this.element.remove()
  }
}

const scriptLoaded = () =>
  [...document.scripts].some((script) => script.src === youtubeApiUrl)

function installApi() {
  window.YT = {
    Player: FakePlayer,
  }
}

afterEach(() => {
  cleanup()
  document
    .querySelectorAll('script')
    .forEach((script) => script.src === youtubeApiUrl && script.remove())
  delete window.YT
  delete window.onYouTubeIframeAPIReady
  FakePlayer.created = 0
  FakePlayer.last = null
  FakePlayer.constructorArgs = []
})

async function startPlaying(user: User) {
  await user.click(screen.getByRole('button', { name: 'Play trailer' }))
  installApi()
  window.onYouTubeIframeAPIReady?.()
  await waitFor(() => expect(FakePlayer.last?.played).toBe(true))
}

const endVideo = () =>
  act(() => {
    FakePlayer.last?.options.events.onStateChange({
      data: 0,
      target: FakePlayer.last!,
    })
  })

it('does not contact YouTube until play is clicked', async () => {
  const user = userEvent.setup()
  const { container } = render(
    <Trailer videoId="M7lc1UVf-VE" backdropUrl={null} />,
  )

  expect(screen.getByRole('button', { name: 'Play trailer' })).toBeTruthy()
  expect(document.querySelector('iframe')).toBeNull()
  expect(scriptLoaded()).toBe(false)
  expect(container.innerHTML).not.toMatch(/youtube|ytimg/i)

  await user.click(screen.getByRole('button', { name: 'Play trailer' }))

  const script = [...document.scripts].find(
    (item) => item.src === youtubeApiUrl,
  )
  expect(script?.async).toBe(true)
  expect(document.querySelector('iframe')).toBeNull()
  expect(container.innerHTML).not.toMatch(/i\.ytimg|img\.youtube/i)
})

it('builds the privacy-enhanced iframe itself and attaches the player to it', async () => {
  const user = userEvent.setup()
  render(<Trailer videoId="M7lc1UVf-VE" backdropUrl="/art.jpg" />)
  await startPlaying(user)

  const frame = document.querySelector('iframe')
  expect(frame?.getAttribute('src')).toBe(
    `https://www.youtube-nocookie.com/embed/M7lc1UVf-VE?enablejsapi=1&rel=0&iv_load_policy=3&playsinline=1&origin=${encodeURIComponent(window.location.origin)}`,
  )
  expect(frame?.title).toBe('Trailer')
  const [element, options] = FakePlayer.constructorArgs
  expect(element).toBe(frame)
  expect(Object.keys(options as object)).toEqual(['events'])
  expect(frame?.hasAttribute('width')).toBe(false)
  expect(frame?.hasAttribute('height')).toBe(false)
})

it('closes when the video ends', async () => {
  const user = userEvent.setup()
  render(<Trailer videoId="M7lc1UVf-VE" backdropUrl="/art.jpg" />)
  await startPlaying(user)

  endVideo()

  expect(FakePlayer.last?.destroyed).toBe(true)
  expect(document.querySelector('iframe')).toBeNull()
  expect(screen.getByRole('button', { name: 'Play trailer' })).toBeTruthy()
})

it('does not build a player after the trailer was closed during load', async () => {
  const user = userEvent.setup()
  render(<Trailer videoId="M7lc1UVf-VE" backdropUrl={null} />)
  await user.click(screen.getByRole('button', { name: 'Play trailer' }))
  const ready = window.onYouTubeIframeAPIReady
  await user.click(screen.getByRole('button', { name: 'Close trailer' }))
  installApi()
  ready?.()

  expect(FakePlayer.created).toBe(0)
  expect(document.querySelector('iframe')).toBeNull()
  expect(screen.getByRole('button', { name: 'Play trailer' })).toBeTruthy()
})

it('closes the player when Escape is pressed', async () => {
  const user = userEvent.setup()
  render(<Trailer videoId="M7lc1UVf-VE" backdropUrl={null} />)
  await startPlaying(user)

  await user.keyboard('{Escape}')

  expect(FakePlayer.last?.destroyed).toBe(true)
  expect(document.querySelector('iframe')).toBeNull()
})

it('only prevents Escape when it closes the trailer', async () => {
  const user = userEvent.setup()
  render(<Trailer videoId="M7lc1UVf-VE" backdropUrl={null} />)
  const escape = () =>
    new KeyboardEvent('keydown', { key: 'Escape', cancelable: true })

  const before = escape()
  document.dispatchEvent(before)
  expect(before.defaultPrevented).toBe(false)

  await startPlaying(user)
  const handled = escape()
  handled.preventDefault()
  document.dispatchEvent(handled)
  expect(document.querySelector('iframe')).toBeTruthy()

  const closing = escape()
  document.dispatchEvent(closing)
  expect(closing.defaultPrevented).toBe(true)
  expect(document.querySelector('iframe')).toBeNull()
})

it('removes the player when playback fails', async () => {
  const user = userEvent.setup()
  render(<Trailer videoId="M7lc1UVf-VE" backdropUrl={null} />)
  await startPlaying(user)

  act(() => {
    FakePlayer.last?.options.events.onError({
      data: 153,
      target: FakePlayer.last!,
    })
  })

  expect(FakePlayer.last?.destroyed).toBe(true)
  expect(document.querySelector('iframe')).toBeNull()
  expect(screen.getByRole('alert').textContent).toBe(
    'The trailer could not be played.',
  )
})

it('moves focus to Close when the trailer opens and keeps it after the frame in tab order', async () => {
  const user = userEvent.setup()
  render(<Trailer videoId="M7lc1UVf-VE" backdropUrl={null} />)
  await startPlaying(user)

  const close = screen.getByRole('button', { name: 'Close trailer' })
  expect(document.activeElement).toBe(close)
  const frame = document.querySelector('iframe')!
  expect(
    frame.compareDocumentPosition(close) & Node.DOCUMENT_POSITION_FOLLOWING,
  ).toBeTruthy()
})

it.each([
  [
    'the Close button',
    async (user: User) => {
      await user.click(screen.getByRole('button', { name: 'Close trailer' }))
    },
  ],
  [
    'Escape',
    async (user: User) => {
      await user.keyboard('{Escape}')
    },
  ],
  ['the video ending', async () => endVideo()],
  [
    'a player error',
    async () => {
      act(() => {
        FakePlayer.last?.options.events.onError({
          data: 101,
          target: FakePlayer.last!,
        })
      })
    },
  ],
])('returns focus to Play when closed by %s', async (_name, closeIt) => {
  const user = userEvent.setup()
  render(<Trailer videoId="M7lc1UVf-VE" backdropUrl={null} />)
  await startPlaying(user)

  await closeIt(user)

  const play = screen.getByRole('button', { name: 'Play trailer' })
  await waitFor(() => expect(document.activeElement).toBe(play))
})

it('recovers from a failed script load and plays on retry', async () => {
  const user = userEvent.setup()
  render(<Trailer videoId="M7lc1UVf-VE" backdropUrl={null} />)
  await user.click(screen.getByRole('button', { name: 'Play trailer' }))
  const failedScript = [...document.scripts].find(
    (item) => item.src === youtubeApiUrl,
  )!
  act(() => {
    failedScript.onerror?.(new Event('error'))
  })

  expect(scriptLoaded()).toBe(false)
  expect(screen.getByRole('alert').textContent).toBe(
    'The trailer could not be played.',
  )
  const play = screen.getByRole('button', { name: 'Play trailer' })
  await waitFor(() => expect(document.activeElement).toBe(play))

  await user.click(play)

  expect(screen.queryByRole('alert')).toBeNull()
  expect(scriptLoaded()).toBe(true)
  installApi()
  window.onYouTubeIframeAPIReady?.()
  await waitFor(() => expect(FakePlayer.last?.played).toBe(true))
  expect(document.querySelector('iframe')).toBeTruthy()
})

it('calls an existing ready callback and restores it on cleanup', async () => {
  const user = userEvent.setup()
  const previous = vi.fn()
  window.onYouTubeIframeAPIReady = previous
  const first = render(<Trailer videoId="M7lc1UVf-VE" backdropUrl={null} />)
  await user.click(screen.getByRole('button', { name: 'Play trailer' }))
  const second = render(<Trailer videoId="M7lc1UVf-VE" backdropUrl={null} />)
  await user.click(
    within(second.container).getByRole('button', { name: 'Play trailer' }),
  )

  installApi()
  window.onYouTubeIframeAPIReady?.()

  expect(previous).toHaveBeenCalledTimes(1)
  await waitFor(() => expect(FakePlayer.created).toBe(2))

  second.unmount()
  first.unmount()
  expect(window.onYouTubeIframeAPIReady).toBe(previous)
})

it('restores the previous ready callback when closed during load', async () => {
  const user = userEvent.setup()
  const previous = vi.fn()
  window.onYouTubeIframeAPIReady = previous
  render(<Trailer videoId="M7lc1UVf-VE" backdropUrl={null} />)
  await user.click(screen.getByRole('button', { name: 'Play trailer' }))
  expect(window.onYouTubeIframeAPIReady).not.toBe(previous)

  await user.click(screen.getByRole('button', { name: 'Close trailer' }))

  expect(window.onYouTubeIframeAPIReady).toBe(previous)
})

it('times out a stalled load, removes the scripts and loads fresh ones on retry', async () => {
  vi.useFakeTimers({ shouldAdvanceTime: false })
  try {
    render(
      <Trailer videoId="M7lc1UVf-VE" backdropUrl={null} loadTimeout={1000} />,
    )
    fireEvent.click(screen.getByRole('button', { name: 'Play trailer' }))
    const widget = document.createElement('script')
    widget.src =
      'https://www.youtube.com/s/player/abc123/www-widgetapi.vflset/www-widgetapi.js'
    document.head.append(widget)
    const stalled = [...document.scripts].find(
      (item) => item.src === youtubeApiUrl,
    )

    act(() => {
      vi.advanceTimersByTime(999)
    })
    expect(screen.queryByRole('alert')).toBeNull()
    act(() => {
      vi.advanceTimersByTime(1)
    })

    expect(screen.getByRole('alert').textContent).toBe(
      'The trailer could not be played.',
    )
    expect(scriptLoaded()).toBe(false)
    expect(widget.isConnected).toBe(false)
    const play = screen.getByRole('button', { name: 'Play trailer' })
    expect(document.activeElement).toBe(play)

    fireEvent.click(play)

    const fresh = [...document.scripts].find(
      (item) => item.src === youtubeApiUrl,
    )
    expect(fresh).toBeTruthy()
    expect(fresh).not.toBe(stalled)
    expect(screen.queryByRole('alert')).toBeNull()
  } finally {
    vi.useRealTimers()
  }
})

it('does not time out once the API has loaded', async () => {
  vi.useFakeTimers()
  try {
    render(
      <Trailer videoId="M7lc1UVf-VE" backdropUrl={null} loadTimeout={1000} />,
    )
    fireEvent.click(screen.getByRole('button', { name: 'Play trailer' }))
    installApi()
    act(() => {
      window.onYouTubeIframeAPIReady?.()
      vi.advanceTimersByTime(5000)
    })

    expect(screen.queryByRole('alert')).toBeNull()
    expect(document.querySelector('iframe')).toBeTruthy()
  } finally {
    vi.useRealTimers()
  }
})

it('ignores a key that is not a YouTube id', () => {
  render(<Trailer videoId="nope" backdropUrl={null} />)

  expect(screen.queryByRole('button', { name: 'Play trailer' })).toBeNull()
  expect(scriptLoaded()).toBe(false)
  expect(document.querySelector('iframe')).toBeNull()
})

it('shows a fallback and no player when there is no trailer', () => {
  const { container } = render(<Trailer videoId={null} backdropUrl={null} />)

  expect(screen.queryByRole('button', { name: 'Play trailer' })).toBeNull()
  expect(container.querySelector('img')).toBeNull()
  expect(container.querySelector('.artwork-fallback')).toBeTruthy()
  expect(scriptLoaded()).toBe(false)
})
