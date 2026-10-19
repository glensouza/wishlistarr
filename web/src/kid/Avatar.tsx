import { AvatarArt, avatars } from './avatarArt'

type Props = { avatar: string; name: string }

export default function Avatar({ avatar, name }: Props) {
  return (
    <span className={`avatar avatar-${avatar}`} aria-hidden="true">
      {avatars.includes(avatar) ? (
        <AvatarArt avatar={avatar} />
      ) : (
        name.charAt(0).toUpperCase()
      )}
    </span>
  )
}
