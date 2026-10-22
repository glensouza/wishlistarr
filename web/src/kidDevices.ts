import { request } from './api'

export type KidDevice = {
  id: number
  name: string
  profileIds: number[]
  isPaired: boolean
  pairingCodeExpiresAt: string | null
  createdAt: string
  pairedAt: string | null
  lastSeenAt: string | null
  isCurrent: boolean
}

export type KidDeviceInput = { name: string; profileIds: number[] }

export type PairingCode = { code: string; expiresAt: string }

const base = '/api/v1/kid-devices'

export const listKidDevices = () => request<KidDevice[]>('GET', base)

export const createKidDevice = (input: KidDeviceInput) =>
  request<{ device: KidDevice } & PairingCode>('POST', base, input)

export const updateKidDevice = (id: number, input: KidDeviceInput) =>
  request<KidDevice>('PUT', `${base}/${id}`, input)

export const newPairingCode = (id: number) =>
  request<PairingCode>('POST', `${base}/${id}/pairing-code`)

export const revokeKidDevice = (id: number) =>
  request<void>('DELETE', `${base}/${id}`)

export const pairThisDevice = (code: string) =>
  request<{ name: string }>('POST', '/api/v1/kid/pair', { code })

export const exitKidMode = (username: string, password: string) =>
  request<void>('POST', '/api/v1/kid/exit', { username, password })

export const parentSignOut = () => request<void>('POST', '/api/v1/auth/logout')
