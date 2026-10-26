import { useCallback, useEffect, useRef, useState } from 'react'
import { useT } from '../i18n'
import Artwork from './Artwork'

export const youtubeApiUrl = 'https://www.youtube.com/iframe_api'
export const youtubeHost = 'https://www.youtube-nocookie.com'

const playerEnded = 0
const loadTimeoutMs = 10000
const widgetScript = /^https:\/\/www\.youtube\.com\/s\/player\/.*www-widgetapi/

export const isYoutubeId = (key: string | null | undefined): key is string =>
  typeof key === 'string' && /^[A-Za-z0-9_-]{11}$/.test(key)

export const embedUrl = (videoId: string, origin: string) =>
  `${youtubeHost}/embed/${videoId}?enablejsapi=1&rel=0&iv_load_policy=3&playsinline=1&origin=${encodeURIComponent(origin)}`

type YoutubePlayer = {
  playVideo: () => void
  destroy: () => void
}

type YoutubeEvent = {
  data: number
  target: YoutubePlayer
}

type PlayerOptions = {
  events: {
    onReady: (event: { target: YoutubePlayer }) => void
    onStateChange: (event: YoutubeEvent) => void
    onError: (event: YoutubeEvent) => void
    onAutoplayBlocked: () => void
  }
}

declare global {
  interface Window {
    YT?: {
      Player: new (
        element: HTMLElement,
        options: PlayerOptions,
      ) => YoutubePlayer
    }
    onYouTubeIframeAPIReady?: () => void
  }
}

function removeYoutubeScripts() {
  document.querySelectorAll('script').forEach((script) => {
    if (script.src === youtubeApiUrl || widgetScript.test(script.src)) {
      script.remove()
    }
  })
  if (!window.YT?.Player) delete window.YT
}

export default function Trailer({
  videoId,
  backdropUrl,
  loadTimeout = loadTimeoutMs,
}: {
  videoId: string | null
  backdropUrl: string | null
  loadTimeout?: number
}) {
  const t = useT()
  const stageRef = useRef<HTMLDivElement>(null)
  const playerRef = useRef<YoutubePlayer | null>(null)
  const readyRef = useRef<{
    ready: () => void
    previous: (() => void) | undefined
  } | null>(null)
  const timerRef = useRef<ReturnType<typeof setTimeout> | null>(null)
  const generation = useRef(0)
  const closeButton = useRef<HTMLButtonElement>(null)
  const playButton = useRef<HTMLButtonElement>(null)
  const wasPlaying = useRef(false)
  const [playing, setPlaying] = useState(false)
  const [failed, setFailed] = useState(false)
  const [blocked, setBlocked] = useState(false)
  const canPlay = isYoutubeId(videoId)

  const stop = useCallback(() => {
    generation.current += 1
    const player = playerRef.current
    playerRef.current = null
    player?.destroy()
    stageRef.current?.replaceChildren()
    setBlocked(false)
    if (timerRef.current) clearTimeout(timerRef.current)
    timerRef.current = null
    const pending = readyRef.current
    if (pending && window.onYouTubeIframeAPIReady === pending.ready) {
      window.onYouTubeIframeAPIReady = pending.previous
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
    if (playing) {
      closeButton.current?.focus()
    } else if (wasPlaying.current) {
      playButton.current?.focus()
    }
    wasPlaying.current = playing
  }, [playing])

  useEffect(() => {
    if (!playing) return
    function onKey(event: KeyboardEvent) {
      if (event.key !== 'Escape' || event.defaultPrevented) return
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
    const frame = document.createElement('iframe')
    frame.src = embedUrl(id, window.location.origin)
    frame.title = t('details.trailerFrame')
    frame.allow = 'autoplay; encrypted-media; picture-in-picture; fullscreen'
    frame.allowFullscreen = true
    stage.appendChild(frame)
    const player = new Player(frame, {
      events: {
        onReady: (event) => {
          if (generation.current !== ticket) {
            event.target.destroy()
            return
          }
          event.target.playVideo()
        },
        onStateChange: (event) => {
          if (event.data === playerEnded) close()
        },
        onAutoplayBlocked: () => {
          if (generation.current === ticket) setBlocked(true)
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
    const begin = () => openPlayer(ticket, id)
    if (window.YT?.Player) {
      begin()
      return
    }
    const previous = window.onYouTubeIframeAPIReady
    const ready = () => {
      previous?.()
      if (timerRef.current) clearTimeout(timerRef.current)
      timerRef.current = null
      begin()
    }
    readyRef.current = { ready, previous }
    window.onYouTubeIframeAPIReady = ready
    timerRef.current = setTimeout(() => {
      if (generation.current !== ticket) return
      removeYoutubeScripts()
      close()
      setFailed(true)
    }, loadTimeout)
    if ([...document.scripts].some((item) => item.src === youtubeApiUrl)) {
      return
    }
    const script = document.createElement('script')
    script.src = youtubeApiUrl
    script.async = true
    script.onerror = () => {
      script.remove()
      if (generation.current !== ticket) return
      close()
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
              <button
                ref={playButton}
                type="button"
                className="trailer-play"
                onClick={play}
              >
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
      {blocked && <p role="status">{t('details.trailerTapPlay')}</p>}
      {failed && <p role="alert">{t('details.trailerFailed')}</p>}
    </>
  )
}
