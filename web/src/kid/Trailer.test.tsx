import { act, cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, expect, it } from 'vitest'
import Trailer, {
  trailerPlayerVars,
  youtubeApiUrl,
  youtubeHost,
} from './Trailer'

type PlayerOptions = ConstructorParameters<
  NonNullable<Window['YT']>['Player']
>[1]

class FakePlayer {
  static created = 0
  static last: FakePlayer | null = null
  options: PlayerOptions
  played = false
  destroyed = false
  iframe: HTMLIFrameElement

  constructor(element: HTMLElement, options: PlayerOptions) {
    FakePlayer.created += 1
    FakePlayer.last = this
    this.options = options
    this.iframe = document.createElement('iframe')
    this.iframe.src = `${options.host}/embed/${options.videoId}`
    element.replaceWith(this.iframe)
    queueMicrotask(() => {
      if (!this.destroyed) options.events.onReady({ target: this })
    })
  }

  playVideo() {
    this.played = true
  }

  destroy() {
    this.destroyed = true
    this.iframe.remove()
  }
}

const scriptLoaded = () =>
  [...document.scripts].some((script) => script.src === youtubeApiUrl)

function installApi() {
  window.YT = {
    Player: FakePlayer,
    PlayerState: { ENDED: 0 },
  }
}

afterEach(() => {
  cleanup()
  document
    .querySelectorAll('script')
    .forEach((script) => script.src === youtubeApiUrl && script.remove())
  delete window.YT
  delete window.YTConfig
  delete window.onYouTubeIframeAPIReady
  FakePlayer.created = 0
  FakePlayer.last = null
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
  expect(script?.src).toBe(youtubeApiUrl)
  expect(script?.async).toBe(true)
  expect(window.YTConfig?.host).toBe(youtubeHost)
  expect(document.querySelector('iframe')).toBeNull()
  expect(container.innerHTML).not.toMatch(/i\.ytimg|img\.youtube/i)
})

it('plays on the privacy-enhanced host and closes when the video ends', async () => {
  const user = userEvent.setup()
  render(<Trailer videoId="M7lc1UVf-VE" backdropUrl="/art.jpg" />)
  await user.click(screen.getByRole('button', { name: 'Play trailer' }))
  installApi()
  window.onYouTubeIframeAPIReady?.()

  await waitFor(() => expect(FakePlayer.last?.played).toBe(true))
  const options = FakePlayer.last?.options
  expect(options?.host).toBe(youtubeHost)
  expect(options?.videoId).toBe('M7lc1UVf-VE')
  expect(options?.playerVars).toEqual({
    ...trailerPlayerVars,
    origin: window.location.origin,
  })
  expect(options?.width).toBeGreaterThanOrEqual(480)
  expect(options?.height).toBeGreaterThanOrEqual(270)
  expect(document.querySelector('iframe')?.getAttribute('src')).toBe(
    `${youtubeHost}/embed/M7lc1UVf-VE`,
  )

  act(() => {
    options?.events.onStateChange({ data: 0, target: FakePlayer.last! })
  })

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
  await user.click(screen.getByRole('button', { name: 'Play trailer' }))
  installApi()
  window.onYouTubeIframeAPIReady?.()
  await waitFor(() => expect(FakePlayer.last?.played).toBe(true))

  await user.keyboard('{Escape}')

  expect(FakePlayer.last?.destroyed).toBe(true)
  expect(document.querySelector('iframe')).toBeNull()
})

it('removes the player when playback fails', async () => {
  const user = userEvent.setup()
  render(<Trailer videoId="M7lc1UVf-VE" backdropUrl={null} />)
  await user.click(screen.getByRole('button', { name: 'Play trailer' }))
  installApi()
  window.onYouTubeIframeAPIReady?.()
  await waitFor(() => expect(FakePlayer.last?.played).toBe(true))

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
