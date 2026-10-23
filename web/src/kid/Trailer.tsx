import { useCallback, useEffect, useRef, useState } from 'react'
import { useT } from '../i18n'
import Artwork from './Artwork'

export const youtubeApiUrl = 'https://www.youtube.com/iframe_api'
export const youtubeHost = 'https://www.youtube-nocookie.com'

export const trailerPlayerVars = {
  rel: 0,
  iv_load_policy: 3,
  playsinline: 1,
  controls: 1,
  disablekb: 0,
  fs: 1,
} as const

const minPlayerWidth = 480
const minPlayerHeight = 270

export const isYoutubeId = (key: string | null | undefined): key is string =>
  typeof key === 'string' && /^[A-Za-z0-9_-]{11}$/.test(key)

type YoutubePlayer = {
  playVideo: () => void
  destroy: () => void
}

type YoutubeEvent = {
  data: number
  target: YoutubePlayer
}

type PlayerOptions = {
  width: number
  height: number
  videoId: string
  host: string
  playerVars: Record<string, string | number>
  events: {
    onReady: (event: { target: YoutubePlayer }) => void
    onStateChange: (event: YoutubeEvent) => void
    onError: (event: YoutubeEvent) => void
  }
}

declare global {
  interface Window {
    YTConfig?: { host?: string }
    YT?: {
      Player: new (
        element: HTMLElement,
        options: PlayerOptions,
      ) => YoutubePlayer
      PlayerState?: { ENDED?: number }
    }
    onYouTubeIframeAPIReady?: () => void
  }
}

export default function Trailer({
  videoId,
  backdropUrl,
}: {
  videoId: string | null
  backdropUrl: string | null
}) {
  const t = useT()
  const stageRef = useRef<HTMLDivElement>(null)
  const playerRef = useRef<YoutubePlayer | null>(null)
  const readyRef = useRef<(() => void) | null>(null)
  const generation = useRef(0)
  const closeButton = useRef<HTMLButtonElement>(null)
  const [playing, setPlaying] = useState(false)
  const [failed, setFailed] = useState(false)
  const canPlay = isYoutubeId(videoId)

  const stop = useCallback(() => {
    generation.current += 1
    const player = playerRef.current
    playerRef.current = null
    player?.destroy()
    stageRef.current?.replaceChildren()
    if (
      readyRef.current &&
      window.onYouTubeIframeAPIReady === readyRef.current
    ) {
      window.onYouTubeIframeAPIReady = undefined
    }
    readyRef.current = null
  }, [])

  const close = useCallback(() => {
    stop()
    setPlaying(false)
  }, [stop])

  useEffect(() => {
    return () => {
      stop()
    }
  }, [stop])

  useEffect(() => {
    if (playing) closeButton.current?.focus()
  }, [playing])

  useEffect(() => {
    if (!playing) return
    function onKey(event: KeyboardEvent) {
      if (event.key !== 'Escape') return
      event.preventDefault()
      close()
    }
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [playing, close])

  function openPlayer(ticket: number, id: string) {
    const stage = stageRef.current
    const Player = window.YT?.Player
    if (!stage || generation.current !== ticket || !Player) return
    stage.replaceChildren()
    const slot = document.createElement('div')
    stage.appendChild(slot)
    const width = Math.max(minPlayerWidth, stage.clientWidth)
    const height = Math.max(minPlayerHeight, Math.round((width * 9) / 16))
    const player = new Player(slot, {
      width,
      height,
      videoId: id,
      host: youtubeHost,
      playerVars: { ...trailerPlayerVars, origin: window.location.origin },
      events: {
        onReady: (event) => {
          if (generation.current !== ticket) {
            event.target.destroy()
            return
          }
          event.target.playVideo()
        },
        onStateChange: (event) => {
          const ended = window.YT?.PlayerState?.ENDED
          if (
            event.data === 0 ||
            (ended !== undefined && event.data === ended)
          ) {
            close()
          }
        },
        onError: () => {
          close()
          setFailed(true)
        },
      },
    })
    if (generation.current !== ticket) {
      player.destroy()
      stage.replaceChildren()
      return
    }
    playerRef.current = player
  }

  function play() {
    if (!isYoutubeId(videoId)) return
    stop()
    const ticket = generation.current
    const id = videoId
    setFailed(false)
    setPlaying(true)
    window.YTConfig = { host: youtubeHost }
    const begin = () => openPlayer(ticket, id)
    if (window.YT?.Player) {
      begin()
      return
    }
    const ready = () => begin()
    readyRef.current = ready
    window.onYouTubeIframeAPIReady = ready
    if ([...document.scripts].some((item) => item.src === youtubeApiUrl)) {
      return
    }
    const script = document.createElement('script')
    script.src = youtubeApiUrl
    script.async = true
    script.onerror = () => {
      if (generation.current !== ticket) return
      setPlaying(false)
      setFailed(true)
    }
    document.head.append(script)
  }

  return (
    <>
      <div className="trailer-stage">
        {!playing && (
          <>
            <Artwork src={backdropUrl} className="title-backdrop" />
            {canPlay && (
              <button type="button" className="trailer-play" onClick={play}>
                {t('details.playTrailer')}
              </button>
            )}
          </>
        )}
        <div ref={stageRef} className="trailer-frame" hidden={!playing} />
        {playing && (
          <button
            ref={closeButton}
            type="button"
            className="trailer-close"
            onClick={close}
          >
            {t('details.closeTrailer')}
          </button>
        )}
      </div>
      {failed && <p role="alert">{t('details.trailerFailed')}</p>}
    </>
  )
}
