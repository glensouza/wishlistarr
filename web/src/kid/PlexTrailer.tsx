import { useEffect, useRef, useState } from 'react'
import { useT } from '../i18n'
import Artwork from './Artwork'

export default function PlexTrailer({
  url,
  backdropUrl,
}: {
  url: string
  backdropUrl: string | null
}) {
  const t = useT()
  const [playing, setPlaying] = useState(false)
  const [failed, setFailed] = useState(false)
  const play = useRef<HTMLButtonElement>(null)
  const closeButton = useRef<HTMLButtonElement>(null)
  const restoreFocus = useRef(false)
  useEffect(() => {
    if (playing) closeButton.current?.focus()
    if (!playing && restoreFocus.current) {
      play.current?.focus()
      restoreFocus.current = false
    }
  }, [playing])
  const safe =
    /^\/api\/v1\/kid\/titles\/(movie|show)\/[1-9][0-9]*\/trailer$/.test(url)
  function close() {
    restoreFocus.current = true
    setPlaying(false)
  }
  return (
    <div
      className="trailer-stage"
      onKeyDown={(event) => {
        if (event.key === 'Escape' && playing) close()
      }}
    >
      {!playing && <Artwork src={backdropUrl} className="title-backdrop" />}
      {safe && !playing && (
        <button
          ref={play}
          type="button"
          className="trailer-play"
          onClick={() => {
            setFailed(false)
            setPlaying(true)
          }}
        >
          {t('details.playTrailer')}
        </button>
      )}
      {safe && playing && (
        <>
          <video
            className="trailer-frame"
            controls
            autoPlay
            playsInline
            src={url}
            aria-label={t('details.trailerFrame')}
            onEnded={close}
            onError={() => {
              setFailed(true)
              close()
            }}
          />
          <button
            ref={closeButton}
            type="button"
            className="trailer-close"
            onClick={close}
          >
            {t('details.closeTrailer')}
          </button>
        </>
      )}
      {failed && <p role="alert">{t('details.trailerFailed')}</p>}
    </div>
  )
}
