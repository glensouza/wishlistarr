import type { ReactElement } from 'react'

const ink = '#2b2118'
const white = '#ffffff'

function Eyes({
  y = 33,
  gap = 9,
  r = 3,
}: {
  y?: number
  gap?: number
  r?: number
}) {
  return (
    <>
      <circle cx={32 - gap} cy={y} r={r} fill={ink} />
      <circle cx={32 + gap} cy={y} r={r} fill={ink} />
    </>
  )
}

const art: Record<string, () => ReactElement> = {
  fox: () => (
    <>
      <path d="M10 8 28 22 14 38Z" fill="#e8742a" />
      <path d="M54 8 36 22 50 38Z" fill="#e8742a" />
      <path d="M14 14 24 23 16 31Z" fill={ink} />
      <path d="M50 14 40 23 48 31Z" fill={ink} />
      <path d="M8 34Q8 20 32 20T56 34Q56 56 32 58T8 34Z" fill="#e8742a" />
      <path d="M8 36Q24 40 32 56 40 40 56 36Q56 56 32 58T8 36Z" fill={white} />
      <Eyes y={34} />
      <circle cx="32" cy="48" r="3.5" fill={ink} />
    </>
  ),
  owl: () => (
    <>
      <path d="M12 10 26 18 12 26Z" fill="#8a5a36" />
      <path d="M52 10 38 18 52 26Z" fill="#8a5a36" />
      <ellipse cx="32" cy="36" rx="22" ry="22" fill="#8a5a36" />
      <circle cx="23" cy="32" r="9" fill={white} />
      <circle cx="41" cy="32" r="9" fill={white} />
      <circle cx="23" cy="32" r="4" fill={ink} />
      <circle cx="41" cy="32" r="4" fill={ink} />
      <path d="M28 40H36L32 49Z" fill="#f2b632" />
    </>
  ),
  bear: () => (
    <>
      <circle cx="14" cy="16" r="8" fill="#8c5a3c" />
      <circle cx="50" cy="16" r="8" fill="#8c5a3c" />
      <circle cx="14" cy="16" r="4" fill="#d9a67f" />
      <circle cx="50" cy="16" r="4" fill="#d9a67f" />
      <circle cx="32" cy="34" r="23" fill="#8c5a3c" />
      <ellipse cx="32" cy="43" rx="10" ry="8" fill="#d9a67f" />
      <Eyes y={30} gap={10} />
      <ellipse cx="32" cy="40" rx="3.5" ry="2.5" fill={ink} />
    </>
  ),
  cat: () => (
    <>
      <path d="M9 8 28 18 11 34Z" fill="#9a9aa8" />
      <path d="M55 8 36 18 53 34Z" fill="#9a9aa8" />
      <path d="M13 15 22 20 14 27Z" fill="#f4a9b8" />
      <path d="M51 15 42 20 50 27Z" fill="#f4a9b8" />
      <ellipse cx="32" cy="37" rx="24" ry="21" fill="#9a9aa8" />
      <ellipse cx="23" cy="33" rx="3" ry="4" fill={ink} />
      <ellipse cx="41" cy="33" rx="3" ry="4" fill={ink} />
      <path d="M29 41H35L32 45Z" fill="#f4a9b8" />
      <path
        d="M32 45Q28 50 24 47M32 45Q36 50 40 47M6 38 18 40M6 46 18 44M58 38 46 40M58 46 46 44"
        fill="none"
        stroke={ink}
        strokeWidth="1.8"
        strokeLinecap="round"
      />
    </>
  ),
  frog: () => (
    <>
      <circle cx="20" cy="18" r="10" fill="#5fae4e" />
      <circle cx="44" cy="18" r="10" fill="#5fae4e" />
      <ellipse cx="32" cy="38" rx="25" ry="20" fill="#5fae4e" />
      <circle cx="20" cy="18" r="6" fill={white} />
      <circle cx="44" cy="18" r="6" fill={white} />
      <circle cx="20" cy="18" r="3" fill={ink} />
      <circle cx="44" cy="18" r="3" fill={ink} />
      <path
        d="M16 40Q32 54 48 40"
        fill="none"
        stroke={ink}
        strokeWidth="2.5"
        strokeLinecap="round"
      />
      <circle cx="28" cy="32" r="1.4" fill={ink} />
      <circle cx="36" cy="32" r="1.4" fill={ink} />
    </>
  ),
  panda: () => (
    <>
      <circle cx="13" cy="16" r="8" fill={ink} />
      <circle cx="51" cy="16" r="8" fill={ink} />
      <circle cx="32" cy="34" r="23" fill={white} />
      <ellipse
        cx="22"
        cy="32"
        rx="6"
        ry="8"
        fill={ink}
        transform="rotate(20 22 32)"
      />
      <ellipse
        cx="42"
        cy="32"
        rx="6"
        ry="8"
        fill={ink}
        transform="rotate(-20 42 32)"
      />
      <circle cx="22.5" cy="32" r="2" fill={white} />
      <circle cx="41.5" cy="32" r="2" fill={white} />
      <ellipse cx="32" cy="42" rx="3.5" ry="2.5" fill={ink} />
      <path
        d="M32 44.5Q29 49 26 47M32 44.5Q35 49 38 47"
        fill="none"
        stroke={ink}
        strokeWidth="1.8"
        strokeLinecap="round"
      />
    </>
  ),
}

export const avatars = Object.keys(art)

export function AvatarArt({ avatar }: { avatar: string }) {
  const draw = art[avatar]
  if (!draw) return null
  return (
    <svg viewBox="0 0 64 64" width="100%" height="100%" focusable="false">
      {draw()}
    </svg>
  )
}
