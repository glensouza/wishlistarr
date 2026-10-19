import { request } from '../api'

export type PickerProfile = {
  id: number
  name: string
  avatar: string
  requiresPin: boolean
}

export type KidMe = { id: string; name: string }

export const listPickerProfiles = () =>
  request<PickerProfile[]>('GET', '/api/v1/kid/profiles')

export const kidSignIn = (profileId: number, pin?: string) =>
  request<PickerProfile>('POST', '/api/v1/kid/signin', { profileId, pin })

export const getKidMe = () => request<KidMe>('GET', '/api/v1/kid/me')

export const kidSignOut = () => request<void>('POST', '/api/v1/kid/signout')

export const pinLength = { min: 4, max: 8 }
