import { useState } from 'react'

export default function Artwork({
  src,
  className,
}: {
  src: string | null
  className: string
}) {
  const [failedSrc, setFailedSrc] = useState<string | null>(null)
  if (!src || failedSrc === src) {
    return (
      <div className={`artwork-fallback ${className}`} aria-hidden="true" />
    )
  }
  return (
    <img
      className={className}
      src={src}
      alt=""
      onError={() => setFailedSrc(src)}
    />
  )
}
