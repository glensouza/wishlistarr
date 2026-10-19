import { useEffect, useRef, useState, type KeyboardEvent } from 'react'
import { useT } from '../i18n'
import { pinLength } from './kidApi'

type Props = {
  name: string
  error: string | null
  busy: boolean
  onSubmit: (pin: string) => void
  onCancel: () => void
}

const digits = ['1', '2', '3', '4', '5', '6', '7', '8', '9']

export default function PinPad({
  name,
  error,
  busy,
  onSubmit,
  onCancel,
}: Props) {
  const t = useT()
  const [pin, setPin] = useState('')
  const heading = useRef<HTMLHeadingElement>(null)

  useEffect(() => {
    heading.current?.focus()
  }, [])

  const add = (digit: string) =>
    setPin((current) =>
      current.length < pinLength.max ? current + digit : current,
    )
  const remove = () => setPin((current) => current.slice(0, -1))
  const canSubmit = pin.length >= pinLength.min && !busy
  const submit = () => {
    if (canSubmit) onSubmit(pin)
  }

  function onKeyDown(event: KeyboardEvent) {
    if (busy) return
    if (event.target instanceof HTMLButtonElement && event.key === 'Enter') {
      return
    }
    if (/^[0-9]$/.test(event.key)) {
      add(event.key)
    } else if (event.key === 'Backspace') {
      remove()
    } else if (event.key === 'Enter') {
      submit()
    } else if (event.key === 'Escape') {
      onCancel()
    }
  }

  return (
    <section
      className="pin-pad"
      aria-labelledby="pin-title"
      onKeyDown={onKeyDown}
    >
      <h1 id="pin-title" ref={heading} tabIndex={-1}>
        {t('pin.title', { name })}
      </h1>
      <div className="pin-dots" aria-hidden="true">
        {Array.from({ length: pinLength.max }, (_, index) => (
          <span key={index} className={index < pin.length ? 'filled' : ''} />
        ))}
      </div>
      <p className="visually-hidden" role="status">
        {t('pin.display', { count: pin.length, max: pinLength.max })}
      </p>
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
      <div className="pin-keys">
        {digits.map((digit) => (
          <button
            key={digit}
            type="button"
            className="key"
            disabled={busy}
            onClick={() => add(digit)}
          >
            {digit}
          </button>
        ))}
        <button
          type="button"
          className="key key-muted"
          disabled={busy || pin === ''}
          aria-label={t('pin.backspace')}
          onClick={remove}
        >
          <span aria-hidden="true">&#9003;</span>
        </button>
        <button
          type="button"
          className="key"
          disabled={busy}
          onClick={() => add('0')}
        >
          0
        </button>
        <button
          type="button"
          className="key key-go"
          disabled={!canSubmit}
          onClick={submit}
        >
          {t('pin.submit')}
        </button>
      </div>
      <button type="button" className="link-button" onClick={onCancel}>
        {t('pin.cancel')}
      </button>
    </section>
  )
}
