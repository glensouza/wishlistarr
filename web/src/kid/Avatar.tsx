const glyphs: Record<string, string> = {
  fox: '🦊',
  owl: '🦉',
  bear: '🐻',
  cat: '🐱',
  frog: '🐸',
  panda: '🐼',
}

type Props = { avatar: string; name: string }

export default function Avatar({ avatar, name }: Props) {
  return (
    <span className={`avatar avatar-${avatar}`} aria-hidden="true">
      {glyphs[avatar] ?? name.charAt(0).toUpperCase()}
    </span>
  )
}
